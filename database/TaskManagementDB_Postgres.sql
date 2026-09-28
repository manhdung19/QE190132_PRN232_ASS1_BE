	-- ============================================================
-- PRN232 Practical Exam 1 — Task Management Database (PostgreSQL)
-- Tables: Department, Project, Task, Tag, TaskTag
-- ============================================================

-- Drop tables in dependency order
DROP TABLE IF EXISTS "TaskTag";
DROP TABLE IF EXISTS "Tag";
DROP TABLE IF EXISTS "Task";
DROP TABLE IF EXISTS "Project";
DROP TABLE IF EXISTS "Department";

-- ============================================================
-- TABLE: Department
-- ============================================================
CREATE TABLE "Department" (
    "DepartmentID"          SERIAL          PRIMARY KEY,
    "DepartmentName"        VARCHAR(100)    NOT NULL,
    "DepartmentDescription" VARCHAR(300)    NOT NULL,
    "IsActive"              BOOLEAN         NOT NULL DEFAULT TRUE
);

-- ============================================================
-- TABLE: Project
-- ============================================================
CREATE TABLE "Project" (
    "ProjectID"     SERIAL          PRIMARY KEY,
    "ProjectName"   VARCHAR(200)    NOT NULL,
    "Description"   TEXT            NULL,
    "StartDate"     DATE            NOT NULL,
    "EndDate"       DATE            NULL,
    "Status"        SMALLINT        NOT NULL DEFAULT 0,
    -- Status: 0 = Not Started, 1 = In Progress, 2 = Completed, 3 = On Hold
    "DepartmentID"  INT             NOT NULL,
    "IsActive"      BOOLEAN         NOT NULL DEFAULT TRUE,
    "CreatedDate"   TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "FK_Project_Department" FOREIGN KEY ("DepartmentID")
        REFERENCES "Department" ("DepartmentID")
);

-- ============================================================
-- TABLE: Task
-- ============================================================
CREATE TABLE "Task" (
    "TaskID"        SERIAL          PRIMARY KEY,
    "Title"         VARCHAR(300)    NOT NULL,
    "Description"   TEXT            NULL,
    "Status"        SMALLINT        NOT NULL DEFAULT 0,
    -- Status: 0 = To Do, 1 = In Progress, 2 = Done, 3 = Cancelled
    "Priority"      SMALLINT        NOT NULL DEFAULT 1,
    -- Priority: 0 = Low, 1 = Medium, 2 = High, 3 = Critical
    "DueDate"       DATE            NULL,
    "ProjectID"     INT             NOT NULL,
    "IsActive"      BOOLEAN         NOT NULL DEFAULT TRUE,
    "CreatedDate"   TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "ModifiedDate"  TIMESTAMP       NULL,
    CONSTRAINT "FK_Task_Project" FOREIGN KEY ("ProjectID")
        REFERENCES "Project" ("ProjectID")
);

-- ============================================================
-- TABLE: Tag
-- ============================================================
CREATE TABLE "Tag" (
    "TagID"     SERIAL          PRIMARY KEY,
    "TagName"   VARCHAR(50)     NOT NULL UNIQUE,
    "Color"     VARCHAR(7)      NULL    -- Hex color code e.g. #3B82F6
);

-- ============================================================
-- TABLE: TaskTag  (many-to-many: Task ↔ Tag)
-- ============================================================
CREATE TABLE "TaskTag" (
    "TaskID"    INT     NOT NULL,
    "TagID"     INT     NOT NULL,
    CONSTRAINT "PK_TaskTag"         PRIMARY KEY ("TaskID", "TagID"),
    CONSTRAINT "FK_TaskTag_Task"    FOREIGN KEY ("TaskID") REFERENCES "Task" ("TaskID"),
    CONSTRAINT "FK_TaskTag_Tag"     FOREIGN KEY ("TagID")  REFERENCES "Tag"  ("TagID")
);

-- ============================================================
-- SEED: Department
-- ============================================================
INSERT INTO "Department" ("DepartmentName", "DepartmentDescription", "IsActive") VALUES
    ('Engineering',     'Responsible for software design, development, and system architecture.',         TRUE),
    ('Product',         'Handles product strategy, roadmaps, and stakeholder alignment.',                 TRUE),
    ('Quality Assurance', 'Ensures product quality through testing, reviews, and process improvement.',  TRUE),
    ('Design',          'Covers UI/UX design, branding, and user research.',                             TRUE),
    ('Operations',      'Manages infrastructure, deployment pipelines, and IT support.',                 TRUE),
    ('Marketing',       'Drives campaigns, content creation, and customer engagement.',                  FALSE);

-- ============================================================
-- SEED: Project
-- ============================================================
INSERT INTO "Project" ("ProjectName", "Description", "StartDate", "EndDate", "Status", "DepartmentID", "IsActive", "CreatedDate") VALUES
    ('Portal Redesign',
     'Complete overhaul of the customer-facing portal with a modern UI and improved performance.',
     '2024-01-15', '2024-06-30', 1, 1, TRUE, '2024-01-10 09:00:00'),

    ('Mobile App v2',
     'Second major release of the mobile application, including offline mode and push notifications.',
     '2024-02-01', '2024-09-30', 1, 1, TRUE, '2024-01-25 10:00:00'),

    ('Internal Dashboard',
     'Analytics and reporting dashboard for department heads.',
     '2024-03-01', '2024-07-31', 0, 2, TRUE, '2024-02-20 11:00:00'),

    ('QA Automation Suite',
     'Build and maintain an automated regression test suite covering all critical user flows.',
     '2024-01-20', '2024-12-31', 1, 3, TRUE, '2024-01-15 08:00:00'),

    ('Design System v1',
     'Create a shared component library and design token set for all products.',
     '2024-04-01', '2024-08-31', 0, 4, TRUE, '2024-03-28 09:00:00'),

    ('Cloud Migration',
     'Migrate on-premise services to AWS, including CI/CD pipeline setup.',
     '2023-10-01', '2024-03-31', 2, 5, TRUE, '2023-09-20 10:00:00'),

    ('SEO & Content Overhaul',
     'Revamp website content and metadata for better search engine visibility.',
     '2024-05-01', NULL, 0, 6, FALSE, '2024-04-25 09:00:00');

-- ============================================================
-- SEED: Tag
-- ============================================================
INSERT INTO "Tag" ("TagName", "Color") VALUES
    ('frontend',    '#3B82F6'),
    ('backend',     '#10B981'),
    ('bug',         '#EF4444'),
    ('feature',     '#8B5CF6'),
    ('refactor',    '#F59E0B'),
    ('urgent',      '#DC2626'),
    ('testing',     '#06B6D4'),
    ('devops',      '#6366F1'),
    ('design',      '#EC4899'),
    ('docs',        '#64748B');

-- ============================================================
-- SEED: Task
-- ============================================================
INSERT INTO "Task" ("Title", "Description", "Status", "Priority", "DueDate", "ProjectID", "IsActive", "CreatedDate") VALUES
    -- Portal Redesign (ProjectID = 1)
    ('Redesign landing page hero section',
     'Update hero copy, layout, and CTA button styling to match the new brand guidelines.',
     2, 2, '2024-02-28', 1, TRUE, '2024-01-16 09:00:00'),

    ('Implement responsive navigation menu',
     'Build a mobile-friendly hamburger menu that collapses on small screens.',
     1, 2, '2024-03-15', 1, TRUE, '2024-01-18 10:00:00'),

    ('Fix broken image assets on product page',
     'Several product images return 404 after the CDN migration.',
     0, 3, '2024-02-10', 1, TRUE, '2024-02-01 08:00:00'),

    ('Write portal migration runbook',
     'Document the step-by-step process to cut over from the old portal to the new one.',
     0, 1, '2024-05-01', 1, TRUE, '2024-02-05 09:00:00'),

    -- Mobile App v2 (ProjectID = 2)
    ('Implement offline mode with local cache',
     'Use SQLite/MMKV to cache the last fetched task list so the app works without internet.',
     1, 3, '2024-05-31', 2, TRUE, '2024-02-02 10:00:00'),

    ('Set up Firebase push notifications',
     'Integrate FCM for Android and APNs for iOS to deliver task reminder notifications.',
     0, 2, '2024-06-15', 2, TRUE, '2024-02-10 11:00:00'),

    ('Refactor authentication module',
     'Replace custom token handling with a shared auth library used across all apps.',
     1, 2, '2024-04-30', 2, TRUE, '2024-02-15 09:00:00'),

    -- Internal Dashboard (ProjectID = 3)
    ('Create project KPI summary widget',
     'Widget showing total tasks, completion rate, and overdue count per project.',
     0, 1, '2024-05-15', 3, TRUE, '2024-03-02 09:00:00'),

    ('Integrate chart library for trend graphs',
     'Use Chart.js or Recharts to render a weekly task completion trend line.',
     0, 1, '2024-06-01', 3, TRUE, '2024-03-05 10:00:00'),

    -- QA Automation Suite (ProjectID = 4)
    ('Write E2E tests for login flow',
     'Cover happy path and error cases (wrong password, locked account) using Playwright.',
     2, 2, '2024-03-31', 4, TRUE, '2024-01-21 08:00:00'),

    ('Write API contract tests for /api/tasks',
     'Validate request/response shapes, status codes, and error payloads using Supertest.',
     1, 2, '2024-04-30', 4, TRUE, '2024-02-01 09:00:00'),

    ('Set up nightly regression pipeline',
     'GitHub Actions workflow that runs the full test suite every night and emails a report.',
     0, 3, '2024-05-15', 4, TRUE, '2024-02-20 10:00:00'),

    -- Design System v1 (ProjectID = 5)
    ('Define color and typography tokens',
     'Document primary, secondary, neutral, and semantic color values plus font scale.',
     1, 2, '2024-05-01', 5, TRUE, '2024-04-02 09:00:00'),

    ('Build Button component with variants',
     'Primary, secondary, ghost, and destructive button variants with accessible focus styles.',
     0, 1, '2024-05-31', 5, TRUE, '2024-04-05 10:00:00'),

    -- Cloud Migration (ProjectID = 6)
    ('Provision RDS PostgreSQL instance',
     'Set up production-grade RDS with automated backups, Multi-AZ, and parameter group tuning.',
     2, 3, '2023-12-15', 6, TRUE, '2023-10-02 08:00:00'),

    ('Migrate CI/CD from Jenkins to GitHub Actions',
     'Rewrite all pipeline stages (build, test, deploy) as GitHub Actions workflows.',
     2, 2, '2024-02-28', 6, TRUE, '2023-11-01 09:00:00'),

    ('Decommission on-premise servers',
     'Coordinate with IT to power down physical servers after traffic is confirmed on AWS.',
     2, 3, '2024-03-31', 6, TRUE, '2024-01-10 10:00:00');

-- ============================================================
-- SEED: TaskTag
-- ============================================================
INSERT INTO "TaskTag" ("TaskID", "TagID") VALUES
    (1,  1), -- landing page → frontend
    (1,  4), -- landing page → feature
    (2,  1), -- nav menu → frontend
    (3,  3), -- broken images → bug
    (3,  1), -- broken images → frontend
    (4,  10),-- runbook → docs
    (5,  2), -- offline mode → backend
    (5,  4), -- offline mode → feature
    (6,  4), -- push notifications → feature
    (7,  2), -- refactor auth → backend
    (7,  5), -- refactor auth → refactor
    (8,  1), -- KPI widget → frontend
    (8,  4), -- KPI widget → feature
    (9,  1), -- chart library → frontend
    (10, 7), -- E2E tests → testing
    (11, 7), -- API tests → testing
    (11, 2), -- API tests → backend
    (12, 7), -- nightly pipeline → testing
    (12, 8), -- nightly pipeline → devops
    (13, 9), -- color tokens → design
    (14, 9), -- Button component → design
    (14, 1), -- Button component → frontend
    (15, 2), -- RDS → backend
    (15, 8), -- RDS → devops
    (16, 8), -- CI/CD migration → devops
    (17, 8); -- decommission → devops

-- ============================================================
-- Verify
-- ============================================================
SELECT
    d."DepartmentName",
    COUNT(DISTINCT p."ProjectID") AS "Projects",
    COUNT(DISTINCT t."TaskID")    AS "Tasks"
FROM "Department" d
LEFT JOIN "Project" p ON d."DepartmentID" = p."DepartmentID"
LEFT JOIN "Task"    t ON p."ProjectID"    = t."ProjectID"
GROUP BY d."DepartmentID", d."DepartmentName"
ORDER BY d."DepartmentID";
