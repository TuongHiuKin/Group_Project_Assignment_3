using AIVES.BLL.Services.ExamReporting;
using AIVES.BLL.Services.VivaInterview;
using Xunit;
using Xunit.Abstractions;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Kiểm tra 3 câu trả lời mẫu dùng để demo: phải qua cổng chất lượng (để sinh được câu
/// hỏi xoáy) và phải trúng từ khoá rubric (để RubricGradingService cho điểm thật).
/// Nếu test này fail thì bộ dữ liệu demo đã hỏng, không phải logic sản phẩm.
/// </summary>
public class DemoAnswerFixturesTests
{
    private readonly ITestOutputHelper _out;
    public DemoAnswerFixturesTests(ITestOutputHelper output) => _out = output;

    private const string Q01 = "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối cơ sở dữ liệu nếu một service Singleton giữ tham chiếu đến DbContext?";
    private const string Q02 = "Trong kiến trúc 3 lớp của hệ thống AIVES, vì sao các Razor Pages và SignalR Hubs ở Presentation Layer không được phép inject trực tiếp Repositories hay DbContext, mà bắt buộc phải thông qua BLL Services?";
    private const string Q03 = "Tại sao trong phương thức JoinSession của VivaHub, hệ thống bắt buộc phải xác thực danh tính người dùng và kiểm tra quyền sở hữu phiên thi trước khi thêm kết nối vào SignalR Group của session đó?";

    private const string A01 =
        "DbContext phải là Scoped vì mỗi HTTP request cần một instance riêng, được tạo khi request bắt đầu và giải phóng khi request kết thúc. " +
        "Nếu đăng ký Singleton thì ChangeTracker sẽ tích lũy entity qua mọi request, gây rò rỉ bộ nhớ và trả về dữ liệu cũ. " +
        "Ngoài ra DbContext không thread-safe: nhiều luồng dùng chung một instance sẽ gây lỗi concurrency và race condition.";

    private const string A02 =
        "Mỗi tầng có trách nhiệm riêng, đó là separation of concerns. " +
        "Razor Pages và SignalR Hubs chỉ nhận request và hiển thị, không chứa business logic và không truy cập trực tiếp DbContext hay EF Core. " +
        "Việc này giảm phụ thuộc giữa các tầng, gọi là loose coupling: tầng Presentation phụ thuộc vào interface của BLL chứ không vào lớp cụ thể, nên dễ unit test và dễ thay thế. " +
        "BLL giữ toàn bộ quy tắc nghiệp vụ, validation dữ liệu, transaction và phân quyền.";

    private const string A03 =
        "Trước khi thêm kết nối vào group, Hub phải xác thực danh tính người dùng thông qua ClaimsPrincipal và claim lấy từ cookie đăng nhập, vì client có thể gửi bất kỳ SessionId nào. " +
        "Sau đó kiểm tra quyền sở hữu: chỉ cho join group khi phiên thi thuộc đúng sinh viên, chặn nghe trộm hoặc giả mạo SessionId của người khác. " +
        "Cuối cùng là quản lý vòng đời kết nối: xử lý OnConnected, OnDisconnected và reconnect, rời group khi ngắt kết nối để không rò rỉ thành viên.";

    public static TheoryData<string, string, string> Fixtures => new()
    {
        { "EXAM01-Q01", Q01, A01 },
        { "EXAM01-Q02", Q02, A02 },
        { "EXAM01-Q03", Q03, A03 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DemoAnswer_passes_quality_gate(string questionId, string question, string answer)
    {
        var quality = AnswerQualityGate.Evaluate(answer, question, 0.90m);
        _out.WriteLine($"{questionId}: {quality.Kind} — {quality.Reason}");
        Assert.True(quality.IsAccepted, $"{questionId} bị cổng chất lượng chặn: {quality.Kind} — {quality.Reason}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DemoAnswer_scores_against_rubric(string questionId, string question, string answer)
    {
        var service = new RubricGradingService();
        var result = service.Grade(questionId, question, answer, 6.0m);

        Assert.NotNull(result);
        _out.WriteLine($"{questionId}: {result!.EarnedScore}/10 — đạt {(result.IsPassed ? "CÓ" : "KHÔNG")}");
        foreach (var o in result.Outcomes)
        {
            _out.WriteLine($"   {(o.IsMet ? "ĐẠT " : "hụt ")} {o.Name} [{string.Join(", ", o.MatchedKeywords)}]");
        }

        // Câu trả lời mẫu phải đạt ít nhất 7/10 để demo chấm điểm trông hợp lý.
        Assert.True(result.EarnedScore >= 7.0m,
            $"{questionId} chỉ đạt {result.EarnedScore}/10 — câu trả lời mẫu chưa trúng đủ từ khoá rubric.");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DemoAnswer_is_long_enough_to_generate_follow_up(string questionId, string question, string answer)
    {
        // Cổng chất lượng yêu cầu tối thiểu 4 từ có nội dung; câu trả lời demo phải dài hơn nhiều
        // để LLM sinh được câu hỏi xoáy có ý nghĩa.
        var wordCount = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _out.WriteLine($"{questionId}: {wordCount} từ");
        Assert.True(wordCount >= 30, $"{questionId} chỉ có {wordCount} từ — quá ngắn để hỏi xoáy.");
    }
}
