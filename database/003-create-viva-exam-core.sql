/*
    AIVES - ASM2 Viva Examination Core Schema (DB-02).

    Creates Viva examination tables:
      - dbo.VivaQuestion
      - dbo.VivaSession
      - dbo.VivaSessionTurn

    Run this complete file in SSMS on the development SQL Server after 001 and 002.
    Idempotent: checks IF OBJECT_ID(...) IS NULL before creating tables.
*/

USE [AIVES];
GO

IF DB_NAME() <> N'AIVES'
BEGIN
    ;THROW 51030, 'Expected the existing AIVES database. No upgrade was performed.', 1;
END;

IF @@TRANCOUNT <> 0
BEGIN
    ;THROW 51031, 'Run this upgrade outside an existing transaction.', 1;
END;

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.VivaQuestion', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaQuestion
        (
            QuestionId VARCHAR(50) NOT NULL,
            ExamId VARCHAR(50) NOT NULL,
            Title NVARCHAR(250) NOT NULL,
            Content NVARCHAR(MAX) NOT NULL,
            TimeLimitSeconds INT NOT NULL
                CONSTRAINT DF_VivaQuestion_TimeLimitSeconds DEFAULT (90),
            AudioUrl NVARCHAR(500) NULL,

            CONSTRAINT PK_VivaQuestion PRIMARY KEY (QuestionId)
        );

        CREATE INDEX IX_VivaQuestion_ExamId
            ON dbo.VivaQuestion (ExamId);
    END;

    IF OBJECT_ID(N'dbo.VivaSession', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaSession
        (
            SessionId UNIQUEIDENTIFIER NOT NULL,
            ExamId VARCHAR(50) NOT NULL,
            StudentId NVARCHAR(50) NOT NULL,
            State TINYINT NOT NULL,
            StartedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,

            CONSTRAINT PK_VivaSession PRIMARY KEY (SessionId)
        );

        CREATE INDEX IX_VivaSession_StudentId_ExamId
            ON dbo.VivaSession (StudentId, ExamId);
    END;

    IF OBJECT_ID(N'dbo.VivaSessionTurn', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaSessionTurn
        (
            TurnId UNIQUEIDENTIFIER NOT NULL,
            SessionId UNIQUEIDENTIFIER NOT NULL,
            QuestionId VARCHAR(50) NOT NULL,
            TurnOrdinal INT NOT NULL,
            AudioUrl NVARCHAR(500) NULL,
            TranscriptText NVARCHAR(MAX) NULL,
            ConfidenceScore DECIMAL(5,2) NULL,
            FollowUpQuestionText NVARCHAR(MAX) NULL,
            FollowUpAudioUrl NVARCHAR(500) NULL,
            CreatedAtUtc DATETIME2(3) NOT NULL,
            CompletedAtUtc DATETIME2(3) NULL,

            CONSTRAINT PK_VivaSessionTurn PRIMARY KEY (TurnId),
            CONSTRAINT FK_VivaSessionTurn_VivaSession
                FOREIGN KEY (SessionId) REFERENCES dbo.VivaSession (SessionId)
                ON DELETE CASCADE ON UPDATE NO ACTION,
            CONSTRAINT FK_VivaSessionTurn_VivaQuestion
                FOREIGN KEY (QuestionId) REFERENCES dbo.VivaQuestion (QuestionId)
                ON DELETE NO ACTION ON UPDATE NO ACTION,
            CONSTRAINT UQ_VivaSessionTurn_SessionId_TurnOrdinal
                UNIQUE (SessionId, TurnOrdinal)
        );

        CREATE INDEX IX_VivaSessionTurn_QuestionId
            ON dbo.VivaSessionTurn (QuestionId);
    END;

    COMMIT TRANSACTION;
    PRINT N'Viva examination core schema created successfully.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
