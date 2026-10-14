-- Integration checks for a DISPOSABLE Assignment 1 test database after upgrade.
-- This script temporarily writes rows inside one transaction and always rolls back.
-- Do not run on production: it updates one seeded Task while checking FK behavior.
BEGIN;
SET LOCAL search_path TO public;
SET LOCAL lock_timeout = '10s';

DO $$
DECLARE
    test_account_id integer;
    test_task_id integer;
BEGIN
    SELECT min("TaskID") INTO test_task_id FROM public."Task";
    IF test_task_id IS NULL THEN
        RAISE EXCEPTION 'Test requires at least one Assignment 1 Task';
    END IF;

    INSERT INTO public."SystemAccount" ("FullName", "Email", "PasswordHash", "Role")
    VALUES ('Schema Test', 'schema-test@example.invalid', 'not-a-real-password-hash', 0)
    RETURNING "AccountID" INTO test_account_id;

    -- Email must be unique.
    BEGIN
        INSERT INTO public."SystemAccount" ("FullName", "Email", "PasswordHash", "Role")
        VALUES ('Duplicate Test', 'schema-test@example.invalid', 'not-a-real-password-hash', 0);
        RAISE EXCEPTION 'Duplicate email was accepted';
    EXCEPTION WHEN unique_violation THEN
        NULL;
    END;

    -- Direct SQL must not bypass normalized email or allowed roles.
    BEGIN
        INSERT INTO public."SystemAccount" ("FullName", "Email", "PasswordHash", "Role")
        VALUES ('Uppercase Test', 'UPPER@example.invalid', 'not-a-real-password-hash', 0);
        RAISE EXCEPTION 'Unnormalized email was accepted';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;
    BEGIN
        INSERT INTO public."SystemAccount" ("FullName", "Email", "PasswordHash", "Role")
        VALUES ('Bad Role Test', 'role-test@example.invalid', 'not-a-real-password-hash', 2);
        RAISE EXCEPTION 'Invalid role was accepted';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;

    UPDATE public."Task" SET "CreatedByID" = test_account_id
    WHERE "TaskID" = test_task_id;

    -- Account with a Task cannot be deleted, even if that Task is inactive.
    UPDATE public."Task" SET "IsActive" = false WHERE "TaskID" = test_task_id;
    BEGIN
        DELETE FROM public."SystemAccount" WHERE "AccountID" = test_account_id;
        RAISE EXCEPTION 'Account with an inactive Task was deleted';
    EXCEPTION WHEN restrict_violation OR foreign_key_violation THEN
        NULL;
    END;
END $$;

ROLLBACK;
