/*
    AIVES - ASM3 Viva Rubric Criteria Schema & Seed Data (DB-01 / Task #1).

    Creates Viva rubric table and seeds baseline rubric criteria for examination EXAM01:
      - dbo.VivaRubricCriteria
      - dbo.VivaQuestion.BloomLevel column

    Run this complete file in SSMS on the development SQL Server after 003/004.
    Idempotent: checks IF OBJECT_ID / COL_LENGTH / NOT EXISTS before creating/inserting.
*/

USE [AIVES];
GO

IF DB_NAME() <> N'AIVES'
BEGIN
    ;THROW 51060, 'Expected the existing AIVES database. No upgrade was performed.', 1;
END;

IF @@TRANCOUNT <> 0
BEGIN
    ;THROW 51061, 'Run this upgrade outside an existing transaction.', 1;
END;

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 1. Ensure BloomLevel column exists in dbo.VivaQuestion.
--    This must be its own batch: SQL Server compiles a batch before executing it, so an ALTER TABLE
--    followed by a statement referencing the new column in the SAME batch fails with
--    "Invalid column name 'BloomLevel'".
IF OBJECT_ID(N'dbo.VivaQuestion', N'U') IS NULL
BEGIN
    ;THROW 51062, 'dbo.VivaQuestion is missing. Create the viva schema before running this seed script.', 1;
END;

IF COL_LENGTH(N'dbo.VivaQuestion', N'BloomLevel') IS NULL
BEGIN
    ALTER TABLE dbo.VivaQuestion ADD BloomLevel NVARCHAR(50) NULL;
END;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- 2. Backfill BloomLevel for existing baseline questions if currently NULL
    UPDATE dbo.VivaQuestion SET BloomLevel = N'Phân tích' WHERE QuestionId = 'EXAM01-Q01' AND BloomLevel IS NULL;
    UPDATE dbo.VivaQuestion SET BloomLevel = N'Hiểu' WHERE QuestionId = 'EXAM01-Q02' AND BloomLevel IS NULL;
    UPDATE dbo.VivaQuestion SET BloomLevel = N'Vận dụng' WHERE QuestionId = 'EXAM01-Q03' AND BloomLevel IS NULL;

    -- 3. Create dbo.VivaRubricCriteria table if missing
    IF OBJECT_ID(N'dbo.VivaRubricCriteria', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VivaRubricCriteria
        (
            Id VARCHAR(50) NOT NULL,
            QuestionId VARCHAR(50) NOT NULL,
            CriterionCode VARCHAR(50) NOT NULL,
            CriterionName NVARCHAR(200) NOT NULL,
            Description NVARCHAR(MAX) NOT NULL,
            Weight DECIMAL(5, 2) NOT NULL,
            MaxPoints DECIMAL(5, 2) NOT NULL,
            KeywordsJson NVARCHAR(MAX) NULL,

            CONSTRAINT PK_VivaRubricCriteria PRIMARY KEY (Id),
            CONSTRAINT FK_VivaRubricCriteria_VivaQuestion FOREIGN KEY (QuestionId)
                REFERENCES dbo.VivaQuestion (QuestionId) ON DELETE CASCADE
        );

        CREATE INDEX IX_VivaRubricCriteria_QuestionId
            ON dbo.VivaRubricCriteria (QuestionId);
    END;

    -- 4. Seed rubric criteria for EXAM01-Q01
    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q01-01')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q01-01',
            'EXAM01-Q01',
            'Q01-C1',
            N'Vòng đời Scoped theo từng HTTP request',
            N'Hiểu đúng DbContext Scoped được tạo một lần cho mỗi HTTP request và giải phóng khi request kết thúc.',
            3.5,
            3.5,
            N'["mỗi request","mỗi http request","từng request","một request","mỗi yêu cầu","từng yêu cầu","per request","request scope","phạm vi request","kết thúc request","hết request","sau request"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q01-02')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q01-02',
            'EXAM01-Q01',
            'Q01-C2',
            N'ChangeTracker tích lũy entity, rò rỉ bộ nhớ',
            N'Phân tích DbContext Singleton giữ kết nối và tích lũy entity trong ChangeTracker gây rò rỉ bộ nhớ, dữ liệu cũ.',
            3.5,
            3.5,
            N'["rò rỉ bộ nhớ","rò rỉ","memory leak","tích lũy","tích tụ","phình to","change tracker","changetracker","dữ liệu cũ","stale","giữ kết nối","không giải phóng","connection pool","cạn kết nối"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q01-03')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q01-03',
            'EXAM01-Q01',
            'Q01-C3',
            N'DbContext không an toàn đa luồng',
            N'Nêu rõ DbContext không thread-safe: nhiều luồng dùng chung một instance sẽ gây lỗi concurrency.',
            3.0,
            3.0,
            N'["đa luồng","nhiều luồng","thread safe","thread-safe","không an toàn luồng","đồng thời","concurrent","concurrency","race condition","multi thread","multithread"]'
        );
    END;

    -- 4. Seed rubric criteria for EXAM01-Q02
    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q02-01')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q02-01',
            'EXAM01-Q02',
            'Q02-C1',
            N'Tách biệt trách nhiệm giữa các tầng',
            N'Presentation chỉ lo giao diện và nhận request, không chứa business logic hay truy cập dữ liệu (Separation of Concerns).',
            3.5,
            3.5,
            N'["tách biệt","separation of concerns","trách nhiệm","mỗi tầng","mỗi lớp","phân tầng","single responsibility","từng tầng"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q02-02')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q02-02',
            'EXAM01-Q02',
            'Q02-C2',
            N'Giảm phụ thuộc, không rò rỉ EF Core lên UI',
            N'Giao diện không truy cập trực tiếp DbContext/EF Core, phụ thuộc qua interface để loose coupling và dễ unit test.',
            3.5,
            3.5,
            N'["loose coupling","decoupling","giảm phụ thuộc","phụ thuộc","interface","repository","unit test","dễ test","kiểm thử","dto","không truy cập trực tiếp"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q02-03')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q02-03',
            'EXAM01-Q02',
            'Q02-C3',
            N'BLL giữ quy tắc nghiệp vụ và validation',
            N'BLL quản lý quy tắc nghiệp vụ, validation, transaction và phân quyền nghiệp vụ.',
            3.0,
            3.0,
            N'["validation","kiểm tra dữ liệu","nghiệp vụ","business logic","business rule","quy tắc","transaction","giao dịch","phân quyền","authorization"]'
        );
    END;

    -- 5. Seed rubric criteria for EXAM01-Q03
    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q03-01')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q03-01',
            'EXAM01-Q03',
            'Q03-C1',
            N'Xác thực danh tính qua ClaimsPrincipal',
            N'Hub yêu cầu đăng nhập và lấy danh tính sinh viên từ claim của ClaimsPrincipal.',
            3.5,
            3.5,
            N'["claimsprincipal","claims principal","claim","xác thực","authenticate","authentication","authorize","đăng nhập","danh tính","token","cookie"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q03-02')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q03-02',
            'EXAM01-Q03',
            'Q03-C2',
            N'Kiểm tra quyền sở hữu SessionId trước khi vào group',
            N'Chỉ cho vào group khi phiên thi thuộc về đúng sinh viên, chặn nghe trộm hoặc giả mạo SessionId.',
            3.5,
            3.5,
            N'["sở hữu","owner","ownership","kiểm tra quyền","đúng sinh viên","nghe trộm","giả mạo","trước khi join","join group","addtogroup","thêm vào group"]'
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.VivaRubricCriteria WHERE Id = 'RC-EXAM01-Q03-03')
    BEGIN
        INSERT INTO dbo.VivaRubricCriteria (Id, QuestionId, CriterionCode, CriterionName, Description, Weight, MaxPoints, KeywordsJson)
        VALUES (
            'RC-EXAM01-Q03-03',
            'EXAM01-Q03',
            'Q03-C3',
            N'Quản lý vòng đời kết nối realtime',
            N'Xử lý kết nối, ngắt kết nối và kết nối lại; rời group khi disconnect.',
            3.0,
            3.0,
            N'["disconnect","ngắt kết nối","ondisconnected","onconnected","reconnect","kết nối lại","vòng đời kết nối","rời group","xóa khỏi group","leave","removefromgroup"]'
        );
    END;

    COMMIT TRANSACTION;
    PRINT N'Viva rubric criteria schema and seed data applied successfully.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
