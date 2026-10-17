namespace AIVES.BLL.Interfaces.Ai;

public interface IAiConnectionTester
{
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
}
