-- Read-only verification after Assignment2_Upgrade.sql.
-- Works with either the original seed or later application data.
BEGIN READ ONLY;
SET LOCAL search_path TO public;

DO $$
DECLARE
    account_table regclass := to_regclass('public."SystemAccount"');
    task_table regclass := to_regclass('public."Task"');
BEGIN
    IF account_table IS NULL OR task_table IS NULL THEN
        RAISE EXCEPTION 'Required tables are missing';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = task_table AND attname = 'CreatedByID'
          AND atttypid = 'integer'::regtype AND NOT attnotnull AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION 'Task.CreatedByID is missing or not nullable integer';
    END IF;
    IF (SELECT count(*) FROM pg_attribute
        WHERE attrelid = account_table AND NOT attisdropped
          AND attname IN ('AccountID', 'FullName', 'Email', 'PasswordHash', 'Role', 'CreatedDate')) <> 6 THEN
        RAISE EXCEPTION 'SystemAccount columns are incomplete';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = account_table AND conname = 'UQ_SystemAccount_Email' AND contype = 'u'
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = account_table AND conname = 'CK_SystemAccount_Role' AND contype = 'c'
    ) OR NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = task_table AND conname = 'FK_Task_SystemAccount_CreatedBy'
          AND contype = 'f' AND confrelid = account_table AND confdeltype = 'r' AND convalidated
    ) THEN
        RAISE EXCEPTION 'Unique, role, or restricted foreign-key constraint is missing';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND tablename = 'Task'
          AND indexname = 'IX_Task_CreatedByID'
    ) THEN
        RAISE EXCEPTION 'Task.CreatedByID index is missing';
    END IF;
END $$;

SELECT 'SystemAccount' AS table_name, count(*) AS rows FROM public."SystemAccount"
UNION ALL
SELECT 'Task', count(*) FROM public."Task";

SELECT count(*) AS legacy_tasks_without_creator
FROM public."Task" WHERE "CreatedByID" IS NULL;
COMMIT;
