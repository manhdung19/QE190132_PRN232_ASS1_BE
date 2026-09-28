-- Read-only checks before the original assignment script is executed.
SET LOCAL search_path TO public;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '60s';
DO $$
BEGIN
    IF current_database() <> 'tasktrack_ass1' THEN
        RAISE EXCEPTION USING ERRCODE = 'P1001', MESSAGE = 'Wrong database; import stopped';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND lower(c.relname) IN ('department', 'project', 'task', 'tag', 'tasktag')
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P1002', MESSAGE = 'Assignment objects already exist; import stopped';
    END IF;
END $$;
SELECT current_database() AS verified_database;
