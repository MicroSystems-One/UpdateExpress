using System.IO;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class RebootCoordinatorTests
{
    [Fact]
    public void A_directory_counts_as_writable_only_when_a_file_can_be_created_in_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(RebootCoordinator.CanCreateFileIn(dir));
            Assert.Empty(Directory.GetFiles(dir));
            Assert.False(RebootCoordinator.CanCreateFileIn(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
