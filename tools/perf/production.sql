-- Run (read-only): ssh wizard 'docker exec -i plate-db sh -c '\''PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=30000" psql -X -v ON_ERROR_STOP=1 -A -t -U "$POSTGRES_USER" -d "$POSTGRES_DB"'\''' < tools/perf/production.sql
-- Emits aggregates only. Account numbers are anonymous and local to this capture.
-- No messages, food names, dates of personal logs, identities, photos or JSON payloads leave wizard.
SELECT json_build_object('section', 'environment', 'captured_at_utc', now(),
 'postgres', version(), 'database_bytes', pg_database_size(current_database()),
 'shared_buffers', current_setting('shared_buffers'), 'max_connections', current_setting('max_connections'),
 'jit', current_setting('jit'), 'connections', (SELECT count(*) FROM pg_stat_activity))::text;

SELECT json_build_object('section', 'tables', 'tables', json_agg(x ORDER BY x.table_name))::text
FROM (SELECT t.table_name,
 (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM %I.%I', t.table_schema, t.table_name), false, true, '')))[1]::text::bigint AS rows,
 pg_table_size(format('%I.%I', t.table_schema, t.table_name)::regclass) AS table_bytes,
 pg_indexes_size(format('%I.%I', t.table_schema, t.table_name)::regclass) AS index_bytes
 FROM information_schema.tables t WHERE t.table_schema = 'public' AND t.table_type = 'BASE TABLE') x;

SELECT json_build_object('section', 'account_counts', 'accounts', json_agg(x ORDER BY x.account))::text
FROM (SELECT 'account_' || row_number() OVER (ORDER BY u."Id") AS account,
 (SELECT count(*) FROM "FoodLog" f WHERE f."UserId" = u."Id") AS food_entries,
 (SELECT count(*) FROM "Bodyweights" b WHERE b."UserId" = u."Id") AS weights,
 (SELECT count(*) FROM "Sessions" s WHERE s."UserId" = u."Id") AS sessions,
 (SELECT count(*) FROM "Sets" s JOIN "SessionExercises" e ON e."Id" = s."SessionExerciseId"
  JOIN "Sessions" w ON w."Id" = e."SessionId" WHERE w."UserId" = u."Id") AS sets,
 (SELECT count(*) FROM "ParseRuns" p WHERE p."UserId" = u."Id") AS parses,
 (SELECT count(*) FROM "LabelScans" l WHERE l."UserId" = u."Id") AS label_scans
 FROM "AspNetUsers" u) x;

SELECT json_build_object('section', 'ai_last_30d', 'parse_runs', count(*),
 'parser_errors', count(*) FILTER (WHERE "ParserError" IS NOT NULL),
 'parser_ms_p50', percentile_cont(.5) WITHIN GROUP (ORDER BY "ParserMs"),
 'parser_ms_p95', percentile_cont(.95) WITHIN GROUP (ORDER BY "ParserMs"),
 'match_ms_p50', percentile_cont(.5) WITHIN GROUP (ORDER BY "MatchMs"),
 'match_ms_p95', percentile_cont(.95) WITHIN GROUP (ORDER BY "MatchMs"))::text
FROM "ParseRuns" WHERE "CreatedAt" >= now() - interval '30 days';

SELECT json_build_object('section', 'labels_last_30d', 'scans', count(*),
 'errors', count(*) FILTER (WHERE "Error" IS NOT NULL),
 'latency_ms_p50', percentile_cont(.5) WITHIN GROUP (ORDER BY "LatencyMs"),
 'latency_ms_p95', percentile_cont(.95) WITHIN GROUP (ORDER BY "LatencyMs"))::text
FROM "LabelScans" WHERE "CreatedAt" >= now() - interval '30 days';

-- Count is cumulative per fingerprint: these are NOT exact last-30-day occurrences.
SELECT json_build_object('section', 'errors_active_last_30d', 'types', json_agg(x))::text
FROM (SELECT "Origin", "Type", count(*) AS fingerprints,
 sum("Count") AS cumulative_occurrences, min("FirstSeenAt") AS earliest_fingerprint,
 max("LastSeenAt") AS latest_occurrence
 FROM "ErrorEvents" WHERE "LastSeenAt" >= now() - interval '30 days'
 GROUP BY "Origin", "Type" ORDER BY cumulative_occurrences DESC) x;
