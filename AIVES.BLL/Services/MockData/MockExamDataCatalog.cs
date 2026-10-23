namespace AIVES.BLL.Services.MockData;

public sealed record MockAccount(string Name, string Email);

/// <summary>Một câu hỏi trong lượt thi mô phỏng: câu trả lời đầu, câu hỏi phụ của AI và câu trả lời câu hỏi phụ.</summary>
public sealed record MockAnswer(string QuestionId, string InitialAnswer, string FollowUpQuestion, string FollowUpAnswer);

public sealed record MockAttempt(string Key, DateTime StartedAtUtc, IReadOnlyList<MockAnswer> Answers);

public sealed record MockStudent(MockAccount Account, IReadOnlyList<MockAttempt> Attempts);

/// <summary>
/// Dữ liệu mô phỏng cho các nhóm chức năng chưa triển khai (Nhóm 2: kỳ thi, Nhóm 4: AI chấm điểm):
/// tài khoản test và lượt thi EXAM01 đã hoàn thành của sinh viên mẫu. Điểm không lưu sẵn mà do
/// <see cref="ExamReporting.RubricGradingService"/> chấm từ transcript theo Rubric giảng viên,
/// nên thống kê lớp và nhận xét cho sinh viên luôn khớp với thiết lập chấm điểm.
/// </summary>
public static class MockExamDataCatalog
{
    public const string ExamId = "EXAM01";

    /// <summary>Mật khẩu chung của mọi tài khoản mô phỏng (chỉ dùng cho môi trường Development).</summary>
    public const string DefaultPassword = "Test@123";

    private const string Q01 = "EXAM01-Q01";
    private const string Q02 = "EXAM01-Q02";
    private const string Q03 = "EXAM01-Q03";

    private const string FollowUpQ01 = "Nếu một service Singleton giữ tham chiếu đến DbContext suốt vòng đời ứng dụng thì điều gì xảy ra sau hàng nghìn lượt truy cập?";
    private const string FollowUpQ02 = "Nếu Razor Page gọi thẳng DbContext thì việc bảo trì và mở rộng hệ thống sẽ gặp khó khăn gì?";
    private const string FollowUpQ03 = "Làm sao hub biết sinh viên đang tham gia đúng phiên thi của chính mình?";

    /// <summary>Tài khoản sinh viên sạch (chưa có lượt thi) để thi thử trực tiếp.</summary>
    public static IReadOnlyList<MockAccount> TestAccounts { get; } = new[]
    {
        new MockAccount("Sinh viên Test 1", "sv.test1@aives.local"),
        new MockAccount("Sinh viên Test 2", "sv.test2@aives.local")
    };

    public static IReadOnlyList<MockStudent> Students { get; } = new[]
    {
        new MockStudent(new MockAccount("Nguyễn Minh Anh", "minhanh@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "DbContext được đăng ký Scoped để mỗi request có một instance riêng, xong việc thì container dispose nó và trả kết nối về cho hệ thống.",
                    FollowUpQ01,
                    "Nếu Singleton giữ DbContext thì change tracker cứ tích lũy entity, gây rò rỉ bộ nhớ và đọc phải dữ liệu cũ. Ngoài ra DbContext không thread safe nên nhiều luồng dùng chung sẽ lỗi."),
                new MockAnswer(Q02,
                    "Kiến trúc 3 lớp giúp tách biệt trách nhiệm: Presentation lo giao diện, BLL xử lý nghiệp vụ và validation, DAL truy cập dữ liệu.",
                    FollowUpQ02,
                    "Nếu gọi thẳng DbContext thì UI dính chặt vào EF Core; dùng interface và repository thì loose coupling, dễ unit test hơn."),
                new MockAnswer(Q03,
                    "Hub gắn Authorize, khi kết nối thì lấy mã sinh viên từ claim trong ClaimsPrincipal.",
                    FollowUpQ03,
                    "Trước khi AddToGroup, hub kiểm tra phiên thi có thuộc sở hữu của sinh viên đó không để chặn giả mạo SessionId.")
            })
        }),
        new MockStudent(new MockAccount("Trần Quốc Bảo", "quocbao@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Scoped nghĩa là mỗi yêu cầu HTTP sẽ nhận một DbContext mới, dùng xong thì được dọn dẹp.",
                    FollowUpQ01,
                    "Singleton giữ DbContext lâu dài thì các entity bị theo dõi tích tụ dần, bộ nhớ phình to và có thể giữ kết nối tới database quá lâu."),
                new MockAnswer(Q02,
                    "Mỗi tầng có một vai trò riêng, Presentation chỉ nhận request và hiển thị.",
                    FollowUpQ02,
                    "Các bước như kiểm tra dữ liệu đầu vào hay transaction phải nằm ở BLL, nếu để ở trang thì rất khó bảo trì."),
                new MockAnswer(Q03,
                    "Người dùng phải đăng nhập, hub đọc danh tính từ cookie của phiên làm việc.",
                    FollowUpQ03,
                    "Hub so owner của session với user hiện tại rồi mới thêm vào group; khi disconnect thì xóa khỏi group và client tự reconnect.")
            })
        }),
        new MockStudent(new MockAccount("Lê Thu Hà", "thuha@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 20, 4, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Em nghĩ Scoped là DbContext chỉ sống trong phạm vi request, hết request thì bị hủy.",
                    FollowUpQ01,
                    "Dạ nếu để Singleton thì em nghĩ ứng dụng sẽ chạy không ổn định, có thể bị lỗi khi lưu thông tin."),
                new MockAnswer(Q02,
                    "Dạ phân tầng để code gọn hơn, giao diện không chứa code làm việc với database.",
                    FollowUpQ02,
                    "Nếu gọi thẳng DbContext thì sau này đổi database rất khó, nên mình nên giảm phụ thuộc bằng cách dùng repository."),
                new MockAnswer(Q03,
                    "Dạ hub cần xác thực người dùng trước, chỉ cho người đã đăng nhập vào.",
                    FollowUpQ03,
                    "Em nghĩ là dựa vào mã phiên thi gửi lên ạ.")
            }),
            new MockAttempt("a2", new DateTime(2026, 10, 22, 4, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "DbContext Scoped được tạo cho từng request và bị dispose khi kết thúc request.",
                    FollowUpQ01,
                    "Vì DbContext không an toàn luồng, nếu Singleton thì nhiều request chạy đồng thời sẽ dùng chung một instance và gây race condition."),
                new MockAnswer(Q02,
                    "Mục tiêu là separation of concerns, mỗi tầng một trách nhiệm.",
                    FollowUpQ02,
                    "UI chỉ gọi service qua interface nên dễ test, còn BLL giữ business logic và validation, DAL lo EF Core."),
                new MockAnswer(Q03,
                    "Hub dùng Authorize và lấy danh tính từ ClaimsPrincipal.",
                    FollowUpQ03,
                    "Server kiểm tra quyền sở hữu phiên thi, nếu không phải đúng sinh viên thì không cho join group.")
            })
        }),
        new MockStudent(new MockAccount("Phạm Gia Huy", "giahuy@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Dạ em nhớ là DbContext không nên để Singleton vì nó không hỗ trợ đa luồng.",
                    FollowUpQ01,
                    "Em chưa rõ lắm phần entity, chắc là sẽ bị lỗi khi truy vấn ạ."),
                new MockAnswer(Q02,
                    "Dạ em nghĩ là để dùng repository cho tiện ạ.",
                    FollowUpQ02,
                    "Gọi thẳng DbContext thì code bị rối và khó sửa."),
                new MockAnswer(Q03,
                    "Dạ em nghĩ SignalR gửi tin nhắn realtime cho các máy trong phòng thi.",
                    FollowUpQ03,
                    "Em chưa tìm hiểu phần này kỹ ạ.")
            })
        }),
        new MockStudent(new MockAccount("Võ Ngọc Lan", "ngoclan@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Nếu dùng Singleton thì DbContext sống mãi nên ChangeTracker theo dõi ngày càng nhiều entity.",
                    FollowUpQ01,
                    "Lúc đó bộ nhớ tăng liên tục, gần như memory leak, và thông tin đọc ra có thể là dữ liệu cũ."),
                new MockAnswer(Q02,
                    "Presentation không truy cập trực tiếp DbContext mà đi qua DTO và service.",
                    FollowUpQ02,
                    "BLL là nơi đặt quy tắc nghiệp vụ và phân quyền, nên UI gọi thẳng DbContext sẽ bỏ qua các bước đó."),
                new MockAnswer(Q03,
                    "Phải kiểm tra ownership của SessionId trước khi join, tránh người khác nghe trộm kết quả.",
                    FollowUpQ03,
                    "Khi client ngắt kết nối thì OnDisconnected sẽ rời group, lúc kết nối lại thì kiểm tra lại từ đầu.")
            })
        }),
        new MockStudent(new MockAccount("Đặng Tuấn Kiệt", "tuankiet@mock.aives.local"), new[]
        {
            new MockAttempt("a1", new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Singleton giữ DbContext thì kết nối không giải phóng được, lâu dần cạn kết nối trong pool.",
                    FollowUpQ01,
                    "Em nghĩ Scoped giúp quản lý vòng đời tốt hơn nhưng em chưa nắm chi tiết."),
                new MockAnswer(Q02,
                    "Mỗi lớp trong kiến trúc có trách nhiệm riêng, giao diện chỉ hiển thị thông tin.",
                    FollowUpQ02,
                    "Nếu trang gọi thẳng DbContext thì code lặp lại ở nhiều nơi và khó bảo trì."),
                new MockAnswer(Q03,
                    "Hub chỉ chấp nhận kết nối có token hợp lệ.",
                    FollowUpQ03,
                    "Khi mất mạng thì client reconnect và server xử lý OnConnected lại.")
            }),
            new MockAttempt("a2", new DateTime(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc), new[]
            {
                new MockAnswer(Q01,
                    "Scoped là per request: mỗi HTTP request một DbContext, request kết thúc thì dispose.",
                    FollowUpQ01,
                    "Singleton sẽ làm change tracker phình to gây rò rỉ bộ nhớ, đồng thời DbContext không thread safe nên các request song song sẽ đụng nhau."),
                new MockAnswer(Q02,
                    "Kiến trúc 3 lớp đảm bảo single responsibility cho từng tầng.",
                    FollowUpQ02,
                    "UI chỉ biết interface của service nên các tầng decoupling với nhau, dễ thay DAL và kiểm thử."),
                new MockAnswer(Q03,
                    "Hub yêu cầu authentication rồi đọc claim mã sinh viên.",
                    FollowUpQ03,
                    "Sau đó so mã sinh viên với chủ sở hữu phiên thi rồi mới AddToGroup.")
            })
        })
    };
}
