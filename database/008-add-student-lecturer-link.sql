/*
    AIVES - Student self-registration support.

    Students can now register themselves from /Account/Register. Every self-registered
    student is assigned to a default lecturer so the lecturer's /ClassReports view can
    scope the students they are responsible for.

    Adds:
      - dbo.SystemAccount.LecturerId  INT NULL, FK -> SystemAccount(AccountId)
      - filtered index on LecturerId (only rows where it is not null)

    Notes:
      - LecturerId stays NULL for lecturers (they do not report to another lecturer),
        for the Admin account, and for accounts created before this script.
      - No cascade: deleting a lecturer must not delete students. ON DELETE NO ACTION,
        and the self-reference maps to DeleteBehavior.ClientNoAction in EF Core.
      - The default lecturer is resolved at registration time by the BLL, which takes
        the first active lecturer (AccountRole = 2) ordered by AccountId, unless
        SystemAccount:DefaultLecturerEmail is configured.

    Run in SSMS against the existing AIVES database, after 001..007.
    Idempotent: guarded by COL_LENGTH / sys foreign_keys / sys.indexes checks.
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
/* Filtered indexes require QUOTED_IDENTIFIER + ANSI_NULLS ON, and SQL Server captures
   those settings at BATCH PARSE TIME. Each GO below starts a new batch, so the SET
   statements must be repeated in every batch that creates a filtered index. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

BEGIN TRANSACTION;

/* 1. Add the column when missing. */
IF COL_LENGTH(N'dbo.SystemAccount', N'LecturerId') IS NULL
BEGIN
    ALTER TABLE dbo.SystemAccount ADD LecturerId INT NULL;
    PRINT 'Added dbo.SystemAccount.LecturerId';
END
ELSE
    PRINT 'dbo.SystemAccount.LecturerId already exists - skipped';
GO

/* 2. Foreign key: a student points at a lecturer account; never cascade. */
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SystemAccount_Lecturer'
      AND parent_object_id = OBJECT_ID(N'dbo.SystemAccount'))
BEGIN
    ALTER TABLE dbo.SystemAccount WITH CHECK
        ADD CONSTRAINT FK_SystemAccount_Lecturer
        FOREIGN KEY (LecturerId) REFERENCES dbo.SystemAccount (AccountId);
    PRINT 'Added FK_SystemAccount_Lecturer';
END
ELSE
    PRINT 'FK_SystemAccount_Lecturer already exists - skipped';
GO

/* 3. Filtered index: only students have a lecturer, so index just those rows.
   The SET options are repeated here because this is a new batch. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_SystemAccount_LecturerId'
      AND object_id = OBJECT_ID(N'dbo.SystemAccount'))
BEGIN
    CREATE INDEX IX_SystemAccount_LecturerId
        ON dbo.SystemAccount (LecturerId)
        WHERE LecturerId IS NOT NULL;
    PRINT 'Created IX_SystemAccount_LecturerId';
END
ELSE
    PRINT 'IX_SystemAccount_LecturerId already exists - skipped';
GO

COMMIT TRANSACTION;
GO

/* Verification: one column, one FK, one index. */
SELECT
    COL_LENGTH(N'dbo.SystemAccount', N'LecturerId') AS LecturerIdBytes,
    (SELECT COUNT(*) FROM sys.foreign_keys
        WHERE name = N'FK_SystemAccount_Lecturer') AS ForeignKeys,
    (SELECT COUNT(*) FROM sys.indexes
        WHERE name = N'IX_SystemAccount_LecturerId') AS Indexes;
GO
