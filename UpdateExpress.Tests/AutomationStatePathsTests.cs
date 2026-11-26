using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// The shared (ProgramData) state folder is used only when the current identity can change the
/// state files in it. A file this identity may not write is simulated with the read-only
/// attribute, which fails an open-for-write the same way a missing ACL right does.
/// </summary>
public sealed class AutomationStatePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ue-state-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly string _fallback;

    public AutomationStatePathsTests()
    {
        _shared = Path.Combine(_root, "shared");
        _fallback = Path.Combine(_root, "user");
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_fallback);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* temp folder; best effort */ }
    }

    private string Put(string name, bool readOnly = false)
    {
        string path = Path.Combine(_shared, name);
        File.WriteAllText(path, "{}");
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    [Fact]
    public void Empty_folder_is_writable_and_the_probe_leaves_nothing_behind()
    {
        Assert.True(AutomationStatePaths.IsDirectoryWritable(_shared));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_shared));
    }

    [Fact]
    public void Missing_folder_is_not_writable()
    {
        Assert.False(AutomationStatePaths.IsDirectoryWritable(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Folder_with_writable_state_files_is_writable_and_the_files_are_untouched()
    {
        string pending = Put("pending-reboot.json");
        Put("applications-install-queue.json");
        DateTime before = File.GetLastWriteTimeUtc(pending);

        Assert.True(AutomationStatePaths.IsDirectoryWritable(_shared));
        Assert.Equal("{}", File.ReadAllText(pending));
        Assert.Equal(before, File.GetLastWriteTimeUtc(pending));
    }

    [Theory]
    [InlineData("pending-reboot.json")]
    [InlineData("applications-prompt.json")]
    [InlineData("critical-install-queue.json")]
    [InlineData("unattended-until-clear.json")]
    [InlineData("windows-automation-status.json")]
    [InlineData("winget-available.json")]
    [InlineData("shared-schedule-notice.json")]
    public void A_state_file_this_identity_cannot_write_makes_the_folder_not_writable(string name)
    {
        Put("general-prompt.json");
        Put(name, readOnly: true);

        Assert.False(AutomationStatePaths.IsDirectoryWritable(_shared));
    }

    [Theory]
    [InlineData("fleet-policy.json")]
    [InlineData("update-policy.json")]
    [InlineData("rmm-status.json")]
    [InlineData("pending-reboot.json.imported")]
    public void A_read_only_file_that_is_not_automation_state_does_not_count(string name)
    {
        Put("pending-reboot.json");
        Put(name, readOnly: true);

        Assert.True(AutomationStatePaths.IsDirectoryWritable(_shared));
    }

    [Fact]
    public void A_state_file_another_process_is_writing_does_not_count_as_missing_rights()
    {
        string pending = Put("pending-reboot.json");
        using var busy = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(AutomationStatePaths.IsDirectoryWritable(_shared));
    }

    [Fact]
    public void Shared_folder_with_only_a_writable_pending_reboot_is_chosen()
    {
        Put("pending-reboot.json");

        Assert.Equal(_shared, AutomationStatePaths.ResolveDirectory(_shared, _fallback));
    }

    [Fact]
    public void Shared_folder_whose_pending_reboot_cannot_be_written_is_not_chosen()
    {
        Put("pending-reboot.json", readOnly: true);

        Assert.Equal(_fallback, AutomationStatePaths.ResolveDirectory(_shared, _fallback));
    }

    [Fact]
    public void Shared_folder_without_automation_files_is_not_chosen()
    {
        Put("update-policy.json");
        Put("rmm-status.json");

        Assert.Equal(_fallback, AutomationStatePaths.ResolveDirectory(_shared, _fallback));
    }

    [Fact]
    public void Missing_shared_folder_is_not_chosen()
    {
        Assert.Equal(_fallback, AutomationStatePaths.ResolveDirectory(Path.Combine(_root, "missing"), _fallback));
    }

    [Fact]
    public void Grant_gives_Users_modify_on_the_folder_on_existing_files_and_on_files_created_later()
    {
        string existing = Put("pending-reboot.json");

        Assert.True(AutomationStatePaths.TryGrantUsersModify(_shared));

        Assert.True(UsersCanModify(new DirectoryInfo(_shared).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(existing).GetAccessControl()));
        string later = Put("drivers-prompt.json");
        Assert.True(UsersCanModify(new FileInfo(later).GetAccessControl()));
    }

    [Fact]
    public void Grant_on_a_missing_folder_fails_quietly()
    {
        Assert.False(AutomationStatePaths.TryGrantUsersModify(Path.Combine(_root, "missing")));
    }

    private static bool UsersCanModify(FileSystemSecurity security)
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(users) &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify)
                return true;
        }
        return false;
    }
}
