/*
    AIVES - ASM3 Viva Grading Schema (G4-DB-04 / Issue #9).

    Creates the AI grading + human-in-the-loop audit tables:
      - dbo.VivaGradingResult      (per-question AI score and teacher finalization)
      - dbo.VivaGradeChangeLog     (immutable audit trail of score changes)

    Table names are singular and MUST match the EF Core model in
    AIVES.DAL/Context/AIVESDbContext.cs (entity.ToTable("VivaGradingResult")).

    Run this complete file in SSMS on the development SQL Server after 003 and 004,
    and before 006. Idempotent: checks IF OBJECT_ID(...) IS NULL before creating tables.

    GradingState values (AIVES.BLL.Models.Grading.GradingState):
      0 = Pending, 1 = AiGraded, 2 = Finalized, 3 = Failed
*/

USE [AIVES];
GO

IF DB_NAME() <> N'AIVES'
BEGIN
    ;THROW 51050, 'Expected the existing AIVES database. No upgrade was performed.', 1;
END;

IF @@TRANCOUNT <> 0
BEGIN
    ;THROW 51051, 'Run this upgrade outside an existing transaction.', 1;
END;

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    -- Grading depends on the viva session/question tables created by 003.
    IF OBJECT_ID(N'dbo.VivaSession', N'U') IS NULL OR OBJECT_ID(N'dbo.VivaQuestion', N'U') IS NULL
    BEGIN
        ;THROW 51052, 'dbo.VivaSession/dbo.VivaQuestion are missing. Run 003-create-viva-exam-core.sql first.', 1;
    END;

    -- 1. dbo.VivaGradingResult
    IF OBJECT_ID(N'dbo.VivaGradingResult', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaGradingResult
        (
            Id UNIQUEIDENTIFIER NOT NULL,
            SessionId UNIQUEIDENTIFIER NOT NULL,
            QuestionId VARCHAR(50) NOT NULL,

            AiScore DECIMAL(5, 2) NULL,
            AiFeedback NVARCHAR(MAX) NULL,
            Strengths NVARCHAR(MAX) NULL,
            Weaknesses NVARCHAR(MAX) NULL,
            CriteriaOutcomesJson NVARCHAR(MAX) NULL,
            AiProvider NVARCHAR(100) NULL,
            AiModel NVARCHAR(100) NULL,
            GradingState TINYINT NOT NULL,

            TeacherFinalScore DECIMAL(5, 2) NULL,
            TeacherNotes NVARCHAR(MAX) NULL,
            FinalizedBy NVARCHAR(100) NULL,
            FinalizedAtUtc DATETIME2(3) NULL,

            RowVersion ROWVERSION NOT NULL,

            CONSTRAINT PK_VivaGradingResult PRIMARY KEY (Id),
            CONSTRAINT FK_VivaGradingResult_VivaSession
                FOREIGN KEY (SessionId) REFERENCES dbo.VivaSession (SessionId)
                ON DELETE CASCADE ON UPDATE NO ACTION,
            CONSTRAINT FK_VivaGradingResult_VivaQuestion
                FOREIGN KEY (QuestionId) REFERENCES dbo.VivaQuestion (QuestionId)
                ON DELETE NO ACTION ON UPDATE NO ACTION,
            CONSTRAINT UQ_VivaGradingResult_SessionId_QuestionId
                UNIQUE (SessionId, QuestionId),
            CONSTRAINT CK_VivaGradingResult_GradingState
                CHECK (GradingState IN (0, 1, 2, 3))
        );

        CREATE INDEX IX_VivaGradingResult_QuestionId
            ON dbo.VivaGradingResult (QuestionId);
    END;

    -- 2. dbo.VivaGradeChangeLog
    IF OBJECT_ID(N'dbo.VivaGradeChangeLog', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaGradeChangeLog
        (
            Id BIGINT IDENTITY(1, 1) NOT NULL,
            GradingResultId UNIQUEIDENTIFIER NOT NULL,

            OldScore DECIMAL(5, 2) NULL,
            NewScore DECIMAL(5, 2) NOT NULL,
            ChangedBy NVARCHAR(100) NOT NULL,
            Reason NVARCHAR(MAX) NOT NULL,
            ChangedAtUtc DATETIME2(3) NOT NULL,

            CONSTRAINT PK_VivaGradeChangeLog PRIMARY KEY (Id),
            CONSTRAINT FK_VivaGradeChangeLog_VivaGradingResult
                FOREIGN KEY (GradingResultId) REFERENCES dbo.VivaGradingResult (Id)
                ON DELETE CASCADE ON UPDATE NO ACTION,
            CONSTRAINT CK_VivaGradeChangeLog_Reason_NotBlank
                CHECK (LEN(LTRIM(RTRIM([Reason]))) > 0)
        );

        CREATE INDEX IX_VivaGradeChangeLog_GradingResultId
            ON dbo.VivaGradeChangeLog (GradingResultId);

        CREATE INDEX IX_VivaGradeChangeLog_GradingResultId_ChangedAtUtc
            ON dbo.VivaGradeChangeLog (GradingResultId, ChangedAtUtc DESC);
    END;

    COMMIT TRANSACTION;
    PRINT N'Viva grading schema created successfully.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
