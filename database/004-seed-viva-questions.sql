/*
    AIVES - ASM2 Viva Questions Seed Data (DB-06).

    Seeds baseline Viva questions for examination EXAM01:
      - EXAM01-Q01: DbContext Lifetime
      - EXAM01-Q02: 3-Layer Boundaries
      - EXAM01-Q03: SignalR Group Security

    Run this complete file in SSMS on the development SQL Server after 003.
    Idempotent: inserts only if QuestionId does not already exist.
*/

USE [AIVES];
GO

IF DB_NAME() <> N'AIVES'
BEGIN
    ;THROW 51040, 'Expected the existing AIVES database. No upgrade was performed.', 1;
END;

IF @@TRANCOUNT <> 0
BEGIN
    ;THROW 51041, 'Run this upgrade outside an existing transaction.', 1;
END;

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.VivaQuestion', N'U') IS NULL
    BEGIN
        ;THROW 51042, 'dbo.VivaQuestion is missing. Create the viva schema before running this seed script.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaQuestion WHERE QuestionId = 'EXAM01-Q01')
    BEGIN
        INSERT INTO dbo.VivaQuestion (QuestionId, ExamId, Title, Content, TimeLimitSeconds, AudioUrl)
        VALUES (
            'EXAM01-Q01',
            'EXAM01',
            N'DbContext Lifetime',
            N'Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối cơ sở dữ liệu nếu một service Singleton giữ tham chiếu đến DbContext?',
            90,
            NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaQuestion WHERE QuestionId = 'EXAM01-Q02')
    BEGIN
        INSERT INTO dbo.VivaQuestion (QuestionId, ExamId, Title, Content, TimeLimitSeconds, AudioUrl)
        VALUES (
            'EXAM01-Q02',
            'EXAM01',
            N'3-Layer Boundaries',
            N'Trong kiến trúc 3 lớp của hệ thống AIVES, vì sao các Razor Pages và SignalR Hubs ở Presentation Layer không được phép inject trực tiếp Repositories hay DbContext, mà bắt buộc phải thông qua BLL Services?',
            90,
            NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaQuestion WHERE QuestionId = 'EXAM01-Q03')
    BEGIN
        INSERT INTO dbo.VivaQuestion (QuestionId, ExamId, Title, Content, TimeLimitSeconds, AudioUrl)
        VALUES (
            'EXAM01-Q03',
            'EXAM01',
            N'SignalR Group Security',
            N'Tại sao trong phương thức JoinSession của VivaHub, hệ thống bắt buộc phải xác thực danh tính người dùng và kiểm tra quyền sở hữu phiên thi trước khi thêm kết nối vào SignalR Group của session đó?',
            90,
            NULL
        );
    END;

    COMMIT TRANSACTION;
    PRINT N'Viva questions seeded successfully.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
