/*
    AIVES - Drop legacy "News" tables (cleanup out-of-scope).

    The project template shipped a News module (Category / Tag / NewsArticle / NewsTag).
    It is NOT part of the three graded function groups:
      - Group 3: AI viva interview core
      - Group 4: AI-assisted grading with human-in-the-loop
      - Group 6: Feedback & reports
    The entity classes, repositories, services, Razor Pages and tests have been removed
    from the source tree. This script removes the leftover schema.

    Run in SSMS against the existing AIVES database, after 001..006.
    Idempotent: every step is guarded by IF OBJECT_ID(...) IS NOT NULL, so re-running is safe
    and running on a database that never had the News tables is a no-op.

    Does NOT touch SystemAccount or any of the six Viva tables.
*/

USE [AIVES];
GO

IF DB_NAME() <> N'AIVES'
BEGIN
    ;THROW 51060, 'Expected the existing AIVES database. No upgrade was performed.', 1;
END

IF @@TRANCOUNT <> 0
BEGIN
    ;THROW 51061, 'Run this upgrade outside an existing transaction.', 1;
END

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

/* 1. NewsTag — join table, must go first (FK to both NewsArticle and Tag). */
IF OBJECT_ID(N'dbo.NewsTag', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_NewsTag_NewsArticle' AND parent_object_id = OBJECT_ID(N'dbo.NewsTag'))
        ALTER TABLE dbo.NewsTag DROP CONSTRAINT FK_NewsTag_NewsArticle;

    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_NewsTag_Tag' AND parent_object_id = OBJECT_ID(N'dbo.NewsTag'))
        ALTER TABLE dbo.NewsTag DROP CONSTRAINT FK_NewsTag_Tag;

    DROP TABLE dbo.NewsTag;
    PRINT 'Dropped dbo.NewsTag';
END
ELSE
    PRINT 'dbo.NewsTag does not exist - skipped';
GO

/* 2. NewsArticle — FK to Category and to SystemAccount (creator/editor). */
IF OBJECT_ID(N'dbo.NewsArticle', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_NewsArticle_Category' AND parent_object_id = OBJECT_ID(N'dbo.NewsArticle'))
        ALTER TABLE dbo.NewsArticle DROP CONSTRAINT FK_NewsArticle_Category;

    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_NewsArticle_CreatedBy' AND parent_object_id = OBJECT_ID(N'dbo.NewsArticle'))
        ALTER TABLE dbo.NewsArticle DROP CONSTRAINT FK_NewsArticle_CreatedBy;

    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_NewsArticle_UpdatedBy' AND parent_object_id = OBJECT_ID(N'dbo.NewsArticle'))
        ALTER TABLE dbo.NewsArticle DROP CONSTRAINT FK_NewsArticle_UpdatedBy;

    DROP TABLE dbo.NewsArticle;
    PRINT 'Dropped dbo.NewsArticle';
END
ELSE
    PRINT 'dbo.NewsArticle does not exist - skipped';
GO

/* 3. Tag */
IF OBJECT_ID(N'dbo.Tag', N'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.Tag;
    PRINT 'Dropped dbo.Tag';
END
ELSE
    PRINT 'dbo.Tag does not exist - skipped';
GO

/* 4. Category */
IF OBJECT_ID(N'dbo.Category', N'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.Category;
    PRINT 'Dropped dbo.Category';
END
ELSE
    PRINT 'dbo.Category does not exist - skipped';
GO

COMMIT TRANSACTION;
GO

/* Verification: expect 0 rows. */
SELECT name AS RemainingLegacyTable
FROM sys.tables
WHERE name IN (N'Category', N'Tag', N'NewsArticle', N'NewsTag');
GO
