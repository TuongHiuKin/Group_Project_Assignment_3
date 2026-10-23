using AIVES.BLL.Models.ExamReporting;

namespace AIVES.BLL.Services.ExamReporting;

/// <summary>
/// Dữ liệu mô phỏng thiết lập chấm điểm của giảng viên cho đề EXAM01 (Nhóm 1 và 4 chưa triển khai):
/// mức Bloom, tiêu chí, trọng số và từ khóa của từng câu. LecturerDashboard và RubricGradingService đều đọc từ đây.
/// Từ khóa viết tự nhiên (có dấu, tiếng Việt và tiếng Anh); khi chấm sẽ được chuẩn hóa bỏ dấu và chữ thường.
/// </summary>
public static class RubricCatalog
{
    public static IReadOnlyList<QuestionRubric> All { get; } = new List<QuestionRubric>
    {
        new()
        {
            QuestionId = "EXAM01-Q01",
            ExamId = "EXAM01",
            Title = "DbContext Lifetime & Entity Tracker",
            BloomLevel = "Phân tích",
            Criteria = new List<RubricCriterion>
            {
                new()
                {
                    Id = "Q01-C1",
                    Name = "Vòng đời Scoped theo từng HTTP request",
                    Description = "Hiểu đúng DbContext Scoped được tạo một lần cho mỗi HTTP request và giải phóng khi request kết thúc.",
                    ImprovementHint = "Nêu rõ DbContext Scoped sống trong phạm vi một HTTP request.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "mỗi request", "mỗi http request", "từng request", "một request", "mỗi yêu cầu", "từng yêu cầu",
                        "per request", "request scope", "phạm vi request", "kết thúc request", "hết request", "sau request"
                    }
                },
                new()
                {
                    Id = "Q01-C2",
                    Name = "ChangeTracker tích lũy entity, rò rỉ bộ nhớ",
                    Description = "Phân tích DbContext Singleton giữ kết nối và tích lũy entity trong ChangeTracker gây rò rỉ bộ nhớ, dữ liệu cũ.",
                    ImprovementHint = "Phân tích hậu quả ChangeTracker giữ entity lâu dài: rò rỉ bộ nhớ, dữ liệu cũ, giữ kết nối.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "rò rỉ bộ nhớ", "rò rỉ", "memory leak", "tích lũy", "tích tụ", "phình to", "change tracker", "changetracker",
                        "dữ liệu cũ", "stale", "giữ kết nối", "không giải phóng", "connection pool", "cạn kết nối"
                    }
                },
                new()
                {
                    Id = "Q01-C3",
                    Name = "DbContext không an toàn đa luồng",
                    Description = "Nêu rõ DbContext không thread-safe: nhiều luồng dùng chung một instance sẽ gây lỗi concurrency.",
                    ImprovementHint = "Giải thích DbContext không an toàn đa luồng khi dùng chung giữa các request đồng thời.",
                    Weight = 3.0m,
                    Keywords = new[]
                    {
                        "đa luồng", "nhiều luồng", "thread safe", "thread-safe", "không an toàn luồng", "đồng thời",
                        "concurrent", "concurrency", "race condition", "multi thread", "multithread"
                    }
                }
            }
        },
        new()
        {
            QuestionId = "EXAM01-Q02",
            ExamId = "EXAM01",
            Title = "3-Layer Boundaries & Decoupling",
            BloomLevel = "Hiểu",
            Criteria = new List<RubricCriterion>
            {
                new()
                {
                    Id = "Q02-C1",
                    Name = "Tách biệt trách nhiệm giữa các tầng",
                    Description = "Presentation chỉ lo giao diện và nhận request, không chứa business logic hay truy cập dữ liệu (Separation of Concerns).",
                    ImprovementHint = "Nêu vai trò riêng của từng tầng Presentation, BLL, DAL.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "tách biệt", "separation of concerns", "trách nhiệm", "mỗi tầng", "mỗi lớp", "phân tầng",
                        "single responsibility", "từng tầng"
                    }
                },
                new()
                {
                    Id = "Q02-C2",
                    Name = "Giảm phụ thuộc, không rò rỉ EF Core lên UI",
                    Description = "Giao diện không truy cập trực tiếp DbContext/EF Core, phụ thuộc qua interface để loose coupling và dễ unit test.",
                    ImprovementHint = "Giải thích vì sao UI không được gọi trực tiếp DbContext và lợi ích của interface/DTO.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "loose coupling", "decoupling", "giảm phụ thuộc", "phụ thuộc", "interface", "repository",
                        "unit test", "dễ test", "kiểm thử", "dto", "không truy cập trực tiếp"
                    }
                },
                new()
                {
                    Id = "Q02-C3",
                    Name = "BLL giữ quy tắc nghiệp vụ và validation",
                    Description = "BLL quản lý quy tắc nghiệp vụ, validation, transaction và phân quyền nghiệp vụ.",
                    ImprovementHint = "Nêu BLL là nơi kiểm tra dữ liệu, quy tắc nghiệp vụ và transaction.",
                    Weight = 3.0m,
                    Keywords = new[]
                    {
                        "validation", "kiểm tra dữ liệu", "nghiệp vụ", "business logic", "business rule",
                        "quy tắc", "transaction", "giao dịch", "phân quyền", "authorization"
                    }
                }
            }
        },
        new()
        {
            QuestionId = "EXAM01-Q03",
            ExamId = "EXAM01",
            Title = "SignalR Group Security & Ownership",
            BloomLevel = "Vận dụng",
            Criteria = new List<RubricCriterion>
            {
                new()
                {
                    Id = "Q03-C1",
                    Name = "Xác thực danh tính qua ClaimsPrincipal",
                    Description = "Hub yêu cầu đăng nhập và lấy danh tính sinh viên từ claim của ClaimsPrincipal.",
                    ImprovementHint = "Nêu cách hub xác thực người dùng (Authorize, claim) trước khi xử lý.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "claimsprincipal", "claims principal", "claim", "xác thực", "authenticate", "authentication",
                        "authorize", "đăng nhập", "danh tính", "token", "cookie"
                    }
                },
                new()
                {
                    Id = "Q03-C2",
                    Name = "Kiểm tra quyền sở hữu SessionId trước khi vào group",
                    Description = "Chỉ cho vào group khi phiên thi thuộc về đúng sinh viên, chặn nghe trộm hoặc giả mạo SessionId.",
                    ImprovementHint = "Giải thích việc kiểm tra phiên thi có thuộc sinh viên trước khi thêm vào group.",
                    Weight = 3.5m,
                    Keywords = new[]
                    {
                        "sở hữu", "owner", "ownership", "kiểm tra quyền", "đúng sinh viên", "nghe trộm", "giả mạo",
                        "trước khi join", "join group", "addtogroup", "thêm vào group"
                    }
                },
                new()
                {
                    Id = "Q03-C3",
                    Name = "Quản lý vòng đời kết nối realtime",
                    Description = "Xử lý kết nối, ngắt kết nối và kết nối lại; rời group khi disconnect.",
                    ImprovementHint = "Nêu cách xử lý khi client ngắt kết nối hoặc kết nối lại.",
                    Weight = 3.0m,
                    Keywords = new[]
                    {
                        "disconnect", "ngắt kết nối", "ondisconnected", "onconnected", "reconnect", "kết nối lại",
                        "vòng đời kết nối", "rời group", "xóa khỏi group", "leave", "removefromgroup"
                    }
                }
            }
        }
    };
}
