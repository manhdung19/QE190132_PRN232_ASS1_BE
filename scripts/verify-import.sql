DO $$
BEGIN
    IF (SELECT count(*) FROM public."Department") <> 6
       OR (SELECT count(*) FROM public."Project") <> 7
       OR (SELECT count(*) FROM public."Task") <> 17
       OR (SELECT count(*) FROM public."Tag") <> 10
       OR (SELECT count(*) FROM public."TaskTag") <> 26 THEN
        RAISE EXCEPTION USING ERRCODE = 'P1003', MESSAGE = 'Unexpected seed counts';
    END IF;
    IF (SELECT count(*) FROM pg_constraint
        WHERE contype = 'f' AND convalidated AND (conrelid, confrelid, conname) IN (
            ('public."Project"'::regclass, 'public."Department"'::regclass, 'FK_Project_Department'),
            ('public."Task"'::regclass, 'public."Project"'::regclass, 'FK_Task_Project'),
            ('public."TaskTag"'::regclass, 'public."Task"'::regclass, 'FK_TaskTag_Task'),
            ('public."TaskTag"'::regclass, 'public."Tag"'::regclass, 'FK_TaskTag_Tag')
        )) <> 4 THEN
        RAISE EXCEPTION USING ERRCODE = 'P1004', MESSAGE = 'Foreign key verification failed';
    END IF;
END $$;
SELECT 'Department' AS table_name, count(*) AS rows FROM public."Department"
UNION ALL SELECT 'Project', count(*) FROM public."Project"
UNION ALL SELECT 'Task', count(*) FROM public."Task"
UNION ALL SELECT 'Tag', count(*) FROM public."Tag"
UNION ALL SELECT 'TaskTag', count(*) FROM public."TaskTag";
SELECT c.relname AS table_name, con.conname, pg_get_constraintdef(con.oid) AS definition
FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relname IN ('Department', 'Project', 'Task', 'Tag', 'TaskTag')
ORDER BY c.relname, con.conname;
