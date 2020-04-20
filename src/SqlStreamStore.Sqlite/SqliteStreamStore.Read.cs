namespace SqlStreamStore
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SqlStreamStore.Streams;

    public partial class SqliteStreamStore
    {
        protected override async Task<ReadStreamPage> ReadStreamForwardsInternal(
            string streamId,
            int fromVersion,
            int count,
            bool prefetch,
            ReadNextStreamPage readNext,
            CancellationToken cancellationToken)
        {
            GuardAgainstDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // If the count is int.MaxValue, TSql will see it as a negative number. 
            // Users shouldn't be using int.MaxValue in the first place anyway.
            var maxRecords = count == int.MaxValue ? count - 1 : count;

            using(var connection = OpenConnection())
            using(var command = connection.CreateCommand())
            {
                var streamProperties = await connection.Streams(streamId)
                    .Properties(initializeIfNotFound:false, cancellationToken);

                if(streamProperties == null)
                {
                    // not found.
                    return new ReadStreamPage(
                        streamId,
                        PageReadStatus.StreamNotFound,
                        fromVersion,
                        StreamVersion.End,
                        StreamVersion.End,
                        StreamVersion.End,
                        ReadDirection.Forward,
                        true,
                        readNext);
                }
                
                return await PrepareStreamResponse(command,
                    streamId,
                    ReadDirection.Forward,
                    fromVersion,
                    prefetch,
                    readNext,
                    streamProperties.Key,
                    maxRecords,
                    streamProperties.MaxAge);
            }
        }

        protected override async Task<ReadStreamPage> ReadStreamBackwardsInternal(
            string streamId,
            int fromStreamVersion,
            int count,
            bool prefetch,
            ReadNextStreamPage readNext,
            CancellationToken cancellationToken)
        {
            GuardAgainstDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // If the count is int.MaxValue, TSql will see it as a negative number. 
            // Users shouldn't be using int.MaxValue in the first place anyway.
            var maxRecords = count == int.MaxValue ? count - 1 : count;
            var streamVersion = fromStreamVersion == StreamVersion.End ? int.MaxValue - 1 : fromStreamVersion;
            using(var connection = OpenConnection())
            {
                var streamProperties = await connection.Streams(streamId)
                    .Properties(initializeIfNotFound: false, cancellationToken);

                if(streamProperties == null)
                {
                    // not found.
                    return new ReadStreamPage(
                        streamId,
                        PageReadStatus.StreamNotFound,
                        fromStreamVersion,
                        StreamVersion.End,
                        StreamVersion.End,
                        StreamVersion.End,
                        ReadDirection.Forward,
                        true,
                        readNext);
                }

                using(var command = connection.CreateCommand())
                {
                    // command.CommandText = @"SELECT messages.position
                    //             FROM messages
                    //             WHERE messages.stream_id_internal = @idOriginal
                    //                 AND messages.stream_version <= @streamVersion
                    //             ORDER BY messages.position DESC
                    //             LIMIT 1;";
                    // command.Parameters.Clear();
                    // command.Parameters.AddWithValue("@idOriginal", streamProperties.Key);
                    // command.Parameters.AddWithValue("@streamVersion", streamVersion);
                    // var position = command.ExecuteScalar<long?>();
                    //
                    // if(position == null)
                    // {
                    //     command.CommandText = @"SELECT streams.position
                    //         FROM streams
                    //         WHERE streams.id_internal = @idOriginal;";
                    //     command.Parameters.Clear();
                    //     command.Parameters.AddWithValue("@idOriginal", streamProperties.Key);
                    //     position = command.ExecuteScalar<long?>();
                    // }

                    var position = command.Connection.Streams(streamId)
                        .AllStreamPosition(ReadDirection.Backward, streamVersion);

                    // if no position, then need to return success with end of stream.
                    if(position == null)
                    {
                        // not found.
                        return new ReadStreamPage(
                            streamId,
                            PageReadStatus.Success,
                            fromStreamVersion,
                            StreamVersion.End,
                            StreamVersion.End,
                            StreamVersion.End,
                            ReadDirection.Backward,
                            true,
                            readNext);
                    }

                    return await PrepareStreamResponse(command,
                        streamId,
                        ReadDirection.Backward,
                        fromStreamVersion,
                        prefetch,
                        readNext,
                        streamProperties.Key,
                        maxRecords,
                        streamProperties.MaxAge);
                }
            }
        }

        private Task<ReadStreamPage> PrepareStreamResponse(
            SqliteCommand command,
            string streamId,
            ReadDirection direction,
            int fromVersion,
            bool prefetch,
            ReadNextStreamPage readNext,
            int streamIdInternal,
            int maxRecords,
            int? maxAge)
        {
            var streamVersion = fromVersion == StreamVersion.End ? int.MaxValue -1 : fromVersion;
            int nextVersion = 0;

            var header = command.Connection.Streams(streamId)
                .Properties()
                .GetAwaiter().GetResult();

            var position = command.Connection.Streams(streamId)
                .AllStreamPosition(direction, streamVersion)
                .GetAwaiter().GetResult();

            var remaining = command.Connection.Streams(streamId)
                .Length(direction, position, CancellationToken.None)
                .GetAwaiter().GetResult();
            
            command.CommandText = @"SELECT messages.event_id,
                               messages.stream_version,
                               messages.[position],
                               messages.created_utc,
                               messages.[type],
                               messages.json_metadata,
                               case when @prefetch then messages.json_data else null end as json_data
                        FROM messages
                        WHERE messages.stream_id_internal = @idInternal 
                        AND CASE 
                                WHEN @forwards THEN messages.[position] >= @position
                                ELSE messages.[position] <= @position
                            END
                        ORDER BY
                            CASE 
                                WHEN @forwards THEN messages.position
                                ELSE -messages.position
                            END
                        LIMIT @count; -- messages";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@idInternal", streamIdInternal);
            command.Parameters.AddWithValue("@position", position);
            command.Parameters.AddWithValue("@prefetch", prefetch);
            command.Parameters.AddWithValue("@count", maxRecords);
            command.Parameters.AddWithValue("@forwards", direction == ReadDirection.Forward);

            bool isEnd = false;
            var filtered = new List<StreamMessage>();
            var messages = new List<(StreamMessage message, int? maxAge)>();

            using(var reader = command.ExecuteReader())
            {
                while(reader.Read())
                {
                    messages.Add((ReadStreamMessage(reader, streamId, prefetch), maxAge));
                }
                
                filtered.AddRange(FilterExpired(messages));
            }

            isEnd = remaining - messages.Count <= 0;

            if(direction == ReadDirection.Forward)
            {
                if(messages.Any())
                {
                    nextVersion = messages.Last().message.StreamVersion + 1;
                }
                else
                {
                    nextVersion = header.Version + 1;
                }
            }
            else if (direction == ReadDirection.Backward)
            {
                if(streamVersion == int.MaxValue - 1 && !messages.Any())
                {
                    nextVersion = StreamVersion.End;
                }

                if(messages.Any())
                {
                    nextVersion = messages.Last().message.StreamVersion - 1;
                }
            }

            var page = new ReadStreamPage(
                streamId,
                status: PageReadStatus.Success,
                fromStreamVersion: fromVersion,
                nextStreamVersion: nextVersion,
                lastStreamVersion: header.Version,
                lastStreamPosition: header.Position,
                direction: direction,
                isEnd: isEnd,
                readNext: readNext,
                messages: filtered.ToArray());

            return Task.FromResult(page);
        }

        private StreamMessage ReadStreamMessage(SqliteDataReader reader, string streamId, bool prefetch)
        {
            var messageId = reader.IsDBNull(0) 
                ? Guid.Empty 
                : reader.GetGuid(0);
            var streamVersion = reader.GetInt32(1);
            var position = reader.GetInt64(2);
            var createdUtc = reader.GetDateTime(3);
            var type = reader.GetString(4);
            var jsonMetadata = reader.IsDBNull(5) ? default : reader.GetString(5);
            var preloadJson = (!reader.IsDBNull(6) && prefetch)
                ? reader.GetTextReader(6).ReadToEnd()
                : default;

            return new StreamMessage(
                streamId,
                messageId,
                streamVersion,
                position,
                createdUtc,
                type,
                jsonMetadata,
                ct => prefetch
                    ? Task.FromResult(preloadJson)
                    : SqliteCommandExtensions.GetJsonData(streamId, streamVersion));
        }
    }
}