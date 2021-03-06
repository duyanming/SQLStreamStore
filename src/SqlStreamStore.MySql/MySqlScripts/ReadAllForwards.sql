DROP PROCEDURE IF EXISTS read_all_forwards;

CREATE PROCEDURE read_all_forwards(_count INT, _position BIGINT)

BEGIN
  SELECT streams.id_original,
         streams.max_age,
         messages.message_id,
         messages.stream_version,
         messages.position - 1,
         messages.created_utc,
         messages.type,
         messages.json_metadata
  FROM messages
         STRAIGHT_JOIN streams ON messages.stream_id_internal = streams.id_internal
  WHERE messages.position >= _position + 1
  ORDER BY messages.position ASC
  LIMIT _count;

END;