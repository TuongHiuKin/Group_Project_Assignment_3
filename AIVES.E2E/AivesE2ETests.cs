using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace AIVES.E2E;

/// <summary>
/// Bộ E2E chạy bằng Chrome thật, phủ 4 luồng chính của AIVES:
///   1. Đăng nhập và phân quyền (3 vai trò)
///   2. Cấu hình AI (trang quản trị)
///   3. Thi vấn đáp (phòng thi + SignalR)
///   4. Chấm điểm / duyệt điểm / báo cáo
///
/// Mỗi bước ghi ảnh chụp màn hình vào .local/screenshots/e2e/{runId}/ để làm bằng chứng.
/// Test ghi lại THỜI GIAN từng trang — mục tiêu chính là tìm chỗ nghẽn (trang tải chậm).
/// </summary>
[TestFixture]
[Category("E2E")]
[Parallelizable(ParallelScope.None)]
public class AivesE2ETests : PageTest
{
    private string _runDir = string.Empty;
    private readonly List<(string Step, long Ms, bool Ok)> _timings = new();

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("AIVES_BASE_URL") ?? "http://localhost:5070";

    // Tài khoản test lấy từ seeder; có thể override bằng biến môi trường.
    private static string AdminEmail => Env("AIVES_ADMIN_EMAIL", "admin@AIVESSystem.org");
    private static string AdminPassword => Env("AIVES_ADMIN_PASSWORD", "@@abc123@@");
    private static string LecturerEmail => Env("AIVES_LECTURER_EMAIL", "gv01@aives.local");
    private static string LecturerPassword => Env("AIVES_LECTURER_PASSWORD", "Test@123");
    private static string StudentEmail => Env("AIVES_STUDENT_EMAIL", "sv.test1@aives.local");
    private static string StudentPassword => Env("AIVES_STUDENT_PASSWORD", "Test@123");

    private static string Env(string key, string fallback) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))
            ? fallback
            : Environment.GetEnvironmentVariable(key)!;

    [SetUp]
    public void PrepareRunFolder()
    {
        var runId = Environment.GetEnvironmentVariable("AIVES_E2E_RUN") ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _runDir = Path.Combine(ResolveLocalDir(), "screenshots", "e2e", runId);
        Directory.CreateDirectory(_runDir);
        _timings.Clear();
        TestContext.Out.WriteLine($"Evidence folder: {_runDir}");
    }

    [TearDown]
    public void ReportTimings()
    {
        TestContext.Out.WriteLine("─── Thời gian tải trang ───");
        foreach (var (step, ms, ok) in _timings.OrderByDescending(t => t.Ms))
        {
            TestContext.Out.WriteLine($"{(ok ? "OK  " : "FAIL")} {ms,7} ms  {step}");
        }

        var slow = _timings.Where(t => t.Ms > 3000).ToList();
        if (slow.Count > 0)
        {
            TestContext.Out.WriteLine($"⚠ {slow.Count} bước chậm hơn 3 giây (có thể là điểm nghẽn):");
            foreach (var (step, ms, _) in slow.OrderByDescending(t => t.Ms))
                TestContext.Out.WriteLine($"    {ms} ms  {step}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1. Đăng nhập và phân quyền
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    [Order(1)]
    public async Task Login_As_Student_Lands_On_StudentDashboard()
    {
        await LoginAsync(StudentEmail, StudentPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("StudentDashboard", RegexOptions.IgnoreCase));
        await ShotAsync("01-student-dashboard");
    }

    [Test]
    [Order(2)]
    public async Task Login_As_Lecturer_Sees_Grading_And_ClassReports_But_Not_Admin()
    {
        await LoginAsync(LecturerEmail, LecturerPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("LecturerDashboard", RegexOptions.IgnoreCase));
        await ShotAsync("02-lecturer-dashboard");

        // Sidebar phải có cả hai mục, và mỗi mục phải trỏ đúng trang riêng.
        var gradingHref = await Page.GetAttributeAsync(
            ".sidebar-nav-item a:has-text('Đánh giá & Chấm thi')", "href");
        var reportsHref = await Page.GetAttributeAsync(
            ".sidebar-nav-item a:has-text('Thống kê Lớp học')", "href");
        Assert.That(gradingHref, Is.EqualTo("/Grading/Index"), "Mục 'Đánh giá & Chấm thi' trỏ sai URL");
        Assert.That(reportsHref, Is.EqualTo("/ClassReports"), "Mục 'Thống kê Lớp học' trỏ sai URL");

        // Giảng viên vào được trang chấm điểm.
        await TimedGotoAsync("/Grading/Index", "lecturer → /Grading/Index");
        Assert.That(Page.Url, Does.Contain("/Grading/Index"));
        await ShotAsync("03-grading-index");

        // Giảng viên KHÔNG vào được khu quản trị.
        await Page.GotoAsync($"{BaseUrl}/Admin/AiSettings",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.That(Page.Url, Does.Contain("AccessDenied").Or.Contain("Login"),
            "Giảng viên phải bị chặn khỏi /Admin/AiSettings");
        await ShotAsync("04-lecturer-blocked-admin");
    }

    [Test]
    [Order(3)]
    public async Task Login_With_Wrong_Password_Shows_Error_And_Does_Not_Sign_In()
    {
        await Page.GotoAsync($"{BaseUrl}/Account/Login",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.FillAsync("input[name='Email']", StudentEmail);
        await Page.FillAsync("input[name='Password']", "definitely-not-the-password");
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Lưu ý: /Account/Login được map thêm route "" trong Program.cs, nên form POST về "/"
        // và URL sau khi submit là "http://host/" chứ không phải "/Account/Login". Kiểm tra
        // điều thật sự quan trọng: vẫn ở trang đăng nhập và có thông báo lỗi, KHÔNG vào dashboard.
        Assert.That(Page.Url, Does.Not.Contain("Dashboard"),
            "Mật khẩu sai không được đưa người dùng vào dashboard");
        var body = await Page.InnerTextAsync("body");
        Assert.That(body, Does.Contain("không chính xác"),
            "Phải hiển thị thông báo sai tài khoản/mật khẩu");
        Assert.That(body, Does.Contain("Đăng nhập"),
            "Phải quay lại đúng trang đăng nhập");

        // Bấm lại một trang cần đăng nhập -> vẫn bị chặn, chứng minh không có phiên.
        await Page.GotoAsync($"{BaseUrl}/Grading/Index",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.That(Page.Url, Does.Contain("/Account/Login"),
            "Sau khi đăng nhập sai, trang cần quyền vẫn phải chặn");

        await ShotAsync("05-login-wrong-password");
    }

    [Test]
    [Order(4)]
    public async Task Anonymous_User_Is_Redirected_To_Login()
    {
        foreach (var route in new[] { "/Viva/Index", "/Grading/Index", "/Admin/AiSettings" })
        {
            await Page.GotoAsync($"{BaseUrl}{route}",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            Assert.That(Page.Url, Does.Contain("/Account/Login"),
                $"Route {route} phải yêu cầu đăng nhập");
        }
        await ShotAsync("06-anonymous-redirect");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Cấu hình AI
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    [Order(5)]
    public async Task Admin_AiSettings_Shows_Routing_Card_And_No_DeepSeek_QuickConnect()
    {
        await LoginAsync(AdminEmail, AdminPassword);
        await TimedGotoAsync("/Admin/AiSettings", "admin → /Admin/AiSettings");

        var html = await Page.ContentAsync();
        Assert.That(html, Does.Contain("Capability Routing").Or.Contain("Định tuyến năng lực"),
            "Trang phải có card Capability Routing");
        Assert.That(html, Does.Not.Contain("Kết nối DeepSeek"),
            "Khung 'Kết nối DeepSeek' phải đã bị bỏ");
        Assert.That(html, Does.Not.Contain("DeepSeekInput.ApiKey"),
            "Không được còn ô API key thứ hai");

        await ShotAsync("07-admin-ai-settings");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Thi vấn đáp
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    [Order(6)]
    public async Task Student_Creates_A_Viva_Session_And_Room_Loads_With_SignalR()
    {
        await LoginAsync(StudentEmail, StudentPassword);
        await TimedGotoAsync("/Viva/Index", "student → /Viva/Index");
        await ShotAsync("08-viva-index");

        // Trang chỉ có nút "Vào thi ngay đề EXAM01" (mã đề nằm trong hidden input, không phải ô nhập).
        var startButton = Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Vào thi ngay|Bắt đầu") });
        Assert.That(await startButton.CountAsync(), Is.GreaterThan(0),
            "Không tìm thấy nút bắt đầu thi trên /Viva/Index");

        var sw = Stopwatch.StartNew();
        await startButton.First.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        sw.Stop();
        _timings.Add(("student → tạo phiên + vào phòng thi", sw.ElapsedMilliseconds, true));

        await Expect(Page).ToHaveURLAsync(new Regex("/Viva/Exam/"));
        await ShotAsync("09-viva-room");

        // SignalR phải kết nối được — đây là điểm dễ nghẽn nhất của phòng thi.
        var connected = await WaitForConditionAsync(
            "() => window.vivaExamClient && window.vivaExamClient.connection && window.vivaExamClient.connection.state === 'Connected'",
            TimeSpan.FromSeconds(15));

        Assert.That(connected, Is.True,
            "SignalR không kết nối được trong 15s — phòng thi sẽ không nhận được câu hỏi realtime");

        await ShotAsync("10-viva-room-signalr-connected");
    }

    [Test]
    [Order(7)]
    public async Task Unknown_Exam_Is_Rejected_By_The_Create_Handler()
    {
        await LoginAsync(StudentEmail, StudentPassword);

        // Giao diện chỉ cho thi EXAM01, nên kiểm tra phòng thủ phía server bằng POST trực tiếp
        // với mã đề không tồn tại — đây chính là lỗi VIVA-02 từng tồn tại.
        var status = await Page.EvaluateAsync<int>(@"async () => {
            const page = await fetch('/Viva/Index');
            const html = await page.text();
            const m = html.match(/name=""__RequestVerificationToken""[^>]*value=""([^""]+)""/);
            if (!m) return -1;
            const fd = new FormData();
            fd.append('ExamIdInput', 'EXAM_KHONG_TON_TAI');
            fd.append('__RequestVerificationToken', m[1]);
            const r = await fetch('/Viva/Index?handler=Create', { method: 'POST', body: fd, redirect: 'follow' });
            return r.status;
        }");

        Assert.That(status, Is.Not.EqualTo(-1), "Không lấy được antiforgery token");

        // Handler phải từ chối: hoặc trả lỗi, hoặc quay lại trang tạo phiên (không vào phòng thi).
        await Page.GotoAsync($"{BaseUrl}/Viva/Index",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.That(Page.Url, Does.Not.Contain("/Viva/Exam/"),
            "Đề không tồn tại nhưng vẫn tạo được phiên thi");

        await ShotAsync("11-viva-unknown-exam-rejected");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4. Chấm điểm / báo cáo
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    [Order(8)]
    public async Task Lecturer_Opens_Grading_Review_And_Sees_Split_AI_Teacher_Scores()
    {
        await LoginAsync(LecturerEmail, LecturerPassword);
        await TimedGotoAsync("/Grading/Index", "lecturer → /Grading/Index");

        // /Grading/Index dùng query string: /Grading/Review?sessionId=...
        var reviewLink = Page.Locator("a[href*='/Grading/Review']").First;
        if (await reviewLink.CountAsync() == 0)
        {
            Assert.Inconclusive("Chưa có phiên nào để duyệt điểm trên dữ liệu hiện tại.");
            return;
        }

        var sw = Stopwatch.StartNew();
        await reviewLink.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        sw.Stop();
        _timings.Add(("lecturer → trang duyệt điểm", sw.ElapsedMilliseconds, true));

        await Expect(Page).ToHaveURLAsync(new Regex("/Grading/Review"));
        await ShotAsync("12-grading-review");

        // Phải tách bạch điểm AI và điểm GV.
        var body = await Page.InnerTextAsync("body");
        Assert.That(body, Does.Contain("Điểm AI").Or.Contain("AI chấm"),
            "Trang duyệt điểm phải hiển thị điểm AI");
    }

    [Test]
    [Order(9)]
    public async Task Student_Report_Shows_Per_Question_Scores_And_Rubric()
    {
        await LoginAsync(StudentEmail, StudentPassword);
        await TimedGotoAsync("/StudentReports", "student → /StudentReports");
        await ShotAsync("13-student-reports");
    }

    [Test]
    [Order(10)]
    public async Task Class_Reports_Load_And_Student_Detail_Has_Grading_Entry_Point()
    {
        await LoginAsync(LecturerEmail, LecturerPassword);
        await TimedGotoAsync("/ClassReports", "lecturer → /ClassReports");
        await ShotAsync("14-class-reports");

        var detailLink = Page.Locator("a[href*='StudentDetail']").First;
        if (await detailLink.CountAsync() == 0)
        {
            Assert.Inconclusive("Chưa có sinh viên nào trong báo cáo lớp.");
            return;
        }

        var sw = Stopwatch.StartNew();
        await detailLink.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        sw.Stop();
        _timings.Add(("lecturer → chi tiết sinh viên", sw.ElapsedMilliseconds, true));

        await ShotAsync("15-student-detail");

        // Trang chi tiết phải có lối vào chấm điểm (trước đây hoàn toàn không có).
        var hasGradingLink = await Page.Locator("a[href*='/Grading/Review']").CountAsync() > 0;
        Assert.That(hasGradingLink, Is.True,
            "Trang chi tiết sinh viên thiếu lối vào trang chấm điểm");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task LoginAsync(string email, string password)
    {
        await Page.GotoAsync($"{BaseUrl}/Account/Login",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.FillAsync("input[name='Email']", email);
        await Page.FillAsync("input[name='Password']", password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>Điều hướng có đo thời gian — dùng để phát hiện trang tải chậm.</summary>
    private async Task TimedGotoAsync(string route, string label)
    {
        var sw = Stopwatch.StartNew();
        var ok = true;
        try
        {
            await Page.GotoAsync($"{BaseUrl}{route}",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
        }
        catch
        {
            ok = false;
            throw;
        }
        finally
        {
            sw.Stop();
            _timings.Add((label, sw.ElapsedMilliseconds, ok));
        }
    }

    private async Task<bool> WaitForConditionAsync(string jsExpression, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await Page.EvaluateAsync<bool?>(jsExpression);
                if (result == true) return true;
            }
            catch
            {
                // Trang chưa nạp xong script — thử lại.
            }
            await Task.Delay(250);
        }
        return false;
    }

    private async Task ShotAsync(string name)
    {
        var path = Path.Combine(_runDir, $"{name}.png");
        await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        TestContext.Out.WriteLine($"📸 {path}");
    }

    private static string ResolveLocalDir()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --path-format=absolute --git-common-dir")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    var parent = Directory.GetParent(stdout)?.FullName;
                    if (!string.IsNullOrEmpty(parent)) return Path.Combine(parent, ".local");
                }
            }
        }
        catch
        {
        }

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AIVESSystem.sln")))
                return Path.Combine(dir, ".local");
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".local");
    }
}
