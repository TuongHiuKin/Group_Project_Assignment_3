namespace AIVES.BLL.Options;

public sealed class MockDataOptions
{
    public const string SectionName = "MockData";

    /// <summary>
    /// When true, the app seeds mock accounts and completed EXAM01 attempts at startup
    /// (see <see cref="Services.MockData.MockExamDataCatalog"/>). Enable only for local development.
    /// </summary>
    public bool SeedOnStartup { get; set; }
}
