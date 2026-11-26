using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// Automation state moves from the folder that also holds fleet-policy.json ("legacy") into its
/// own State subfolder ("shared"), and the Users modify grant on the legacy folder is taken back.
/// Everything runs on temp folders with real ACLs; the PC's ProgramData folder is not touched.
/// </summary>
public sealed class LegacyStateMigrationTests : IDisposable
{
    private readonly string _legacy = Path.Combine(Path.GetTempPath(), "ue-legacy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "ue-outside-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = new();
    private readonly string _shared;

    public LegacyStateMigrationTests()
    {
        _shared = Path.Combine(_legacy, "State");
        Directory.CreateDirectory(_legacy);
    }

    public void Dispose()
    {
        foreach (string junction in _junctions)
        {
            try { Directory.Delete(junction); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(_outside)) Directory.Delete(_outside, recursive: true); } catch { /* best effort */ }
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var folders = new List<DirectoryInfo> { new(_legacy) };
            folders.AddRange(new DirectoryInfo(_legacy).EnumerateDirectories("*", SearchOption.AllDirectories));
            foreach (DirectoryInfo folder in folders)
            {
                var security = folder.GetAccessControl();
                security.PurgeAccessRules(me);
                folder.SetAccessControl(security);
            }
            foreach (FileInfo file in new DirectoryInfo(_legacy).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var security = file.GetAccessControl();
                security.PurgeAccessRules(me);
                file.SetAccessControl(security);
                file.Attributes = FileAttributes.Normal;
            }
            Directory.Delete(_legacy, recursive: true);
        }
        catch { /* temp folder; best effort */ }
    }

    private string PutLegacy(string name, string content = "{}", DateTime? writtenUtc = null) =>
        Put(_legacy, name, content, writtenUtc);

    private string PutShared(string name, string content = "{}", DateTime? writtenUtc = null) =>
        Put(_shared, name, content, writtenUtc);

    private static string Put(string dir, string name, string content, DateTime? writtenUtc)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        if (writtenUtc is DateTime at) File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    private int Migrate() => AutomationStatePaths.MigrateLegacyState(_legacy, _shared);

    /// <summary>What a user gets on a file an elevated helper wrote in a folder the user does not own.</summary>
    private void MakeUnremovable(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var file = new FileInfo(path);
        var fileSecurity = file.GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.Delete, AccessControlType.Deny));
        file.SetAccessControl(fileSecurity);

        var folder = new DirectoryInfo(_legacy);
        var folderSecurity = folder.GetAccessControl();
        folderSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
        folder.SetAccessControl(folderSecurity);
    }

    private void MakeRemovable(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var file = new FileInfo(path);
        var fileSecurity = file.GetAccessControl();
        fileSecurity.PurgeAccessRules(me);
        file.SetAccessControl(fileSecurity);

        var folder = new DirectoryInfo(_legacy);
        var folderSecurity = folder.GetAccessControl();
        folderSecurity.PurgeAccessRules(me);
        folder.SetAccessControl(folderSecurity);
    }

    private static void Rewrite(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    [Fact]
    public void State_has_its_own_subfolder_and_the_admin_files_stay_in_the_folder_above()
    {
        string legacy = AutomationStatePaths.LegacySharedDirectory;

        Assert.Equal(Path.Combine(legacy, "State"), AutomationStatePaths.SharedDirectory);
        Assert.Equal(legacy, Path.GetDirectoryName(FleetPolicy.ProgramDataPath));
        Assert.Equal(legacy, Path.GetDirectoryName(UpdatePolicy.MachinePathName));
        Assert.Equal(legacy, RmmStatusExport.ProgramDataDir);
    }

    [Fact]
    public void Every_state_file_moves_and_every_other_file_stays()
    {
        string[] state =
        {
            "pending-reboot.json", "unattended-until-clear.json", "windows-automation-status.json",
            "winget-available.json", "shared-schedule-notice.json",
            "applications-prompt.json", "applications-install-queue.json",
            "critical-prompt.json", "general-install-queue.json", "drivers-prompt.json"
        };
        string[] other = { "fleet-policy.json", "update-policy.json", "rmm-status.json", "rmm-status.txt" };
        foreach (string name in state) PutLegacy(name, "state:" + name);
        foreach (string name in other) PutLegacy(name, "other:" + name);

        Assert.Equal(state.Length, Migrate());

        foreach (string name in state)
        {
            Assert.False(File.Exists(Path.Combine(_legacy, name)), name + " left behind");
            Assert.Equal("state:" + name, File.ReadAllText(Path.Combine(_shared, name)));
        }
        foreach (string name in other)
        {
            Assert.Equal("other:" + name, File.ReadAllText(Path.Combine(_legacy, name)));
            Assert.False(File.Exists(Path.Combine(_shared, name)), name + " moved");
        }
        Assert.Equal(other.Length, Directory.GetFiles(_legacy).Length);
        Assert.Equal(state.Length, Directory.GetFiles(_shared).Length);
        Assert.Equal(_shared, AutomationStatePaths.ResolveDirectory(_shared, Path.Combine(_legacy, "user")));
    }

    [Fact]
    public void A_scheduled_restart_arrives_unchanged()
    {
        var written = new DateTime(2026, 11, 25, 22, 15, 0, DateTimeKind.Utc);
        const string json = "{\"EarliestRebootAt\":\"2026-11-26T03:00:00\",\"Cancelled\":false,\"Source\":\"reminder\"}";
        PutLegacy("pending-reboot.json", json, written);

        Assert.Equal(1, Migrate());

        string moved = Path.Combine(_shared, "pending-reboot.json");
        Assert.Equal(json, File.ReadAllText(moved));
        Assert.Equal(written, File.GetLastWriteTimeUtc(moved));
        Assert.False(File.Exists(Path.Combine(_legacy, "pending-reboot.json")));
    }

    [Fact]
    public void The_new_folder_gets_the_Users_grant_and_the_old_folder_does_not()
    {
        PutLegacy("fleet-policy.json");
        string old = PutLegacy("applications-install-queue.json");
        File.SetAttributes(old, FileAttributes.ReadOnly);

        Assert.Equal(1, Migrate());

        string moved = Path.Combine(_shared, "applications-install-queue.json");
        Assert.True(UsersCanModify(new DirectoryInfo(_shared).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(moved).GetAccessControl()));
        Assert.False(File.GetAttributes(moved).HasFlag(FileAttributes.ReadOnly));
        Assert.False(UsersCanModify(new DirectoryInfo(_legacy).GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(Path.Combine(_legacy, "fleet-policy.json")).GetAccessControl()));
        Assert.True(AutomationStatePaths.IsDirectoryWritable(_shared));
    }

    [Fact]
    public void Nothing_to_move_creates_nothing()
    {
        PutLegacy("fleet-policy.json");
        PutLegacy("update-policy.json");

        Assert.Equal(0, Migrate());
        Assert.False(Directory.Exists(_shared));
        Assert.Equal(0, AutomationStatePaths.MigrateLegacyState(Path.Combine(_legacy, "missing"), _shared));
    }

    [Fact]
    public void A_newer_file_in_the_new_folder_wins_and_the_old_one_is_retired()
    {
        var now = DateTime.UtcNow;
        PutLegacy("pending-reboot.json", "old", now.AddHours(-2));
        PutShared("pending-reboot.json", "new", now.AddHours(-1));

        Assert.Equal(0, Migrate());

        Assert.Equal("new", File.ReadAllText(Path.Combine(_shared, "pending-reboot.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "pending-reboot.json")));
    }

    [Fact]
    public void A_newer_file_in_the_old_folder_replaces_the_one_in_the_new_folder()
    {
        var now = DateTime.UtcNow;
        PutLegacy("applications-install-queue.json", "written by an agent that was still running", now.AddHours(-1));
        PutShared("applications-install-queue.json", "earlier", now.AddHours(-2));

        Assert.Equal(1, Migrate());

        Assert.Equal("written by an agent that was still running", File.ReadAllText(Path.Combine(_shared, "applications-install-queue.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "applications-install-queue.json")));
    }

    [Fact]
    public void A_file_another_process_has_open_stays_complete_and_moves_on_the_next_pass()
    {
        string pending = PutLegacy("pending-reboot.json", "restart");
        PutLegacy("general-prompt.json");

        using (new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, Migrate());
            Assert.True(File.Exists(pending));
            Assert.False(File.Exists(Path.Combine(_shared, "pending-reboot.json")));
        }

        Assert.Equal(1, Migrate());
        Assert.Equal("restart", File.ReadAllText(Path.Combine(_shared, "pending-reboot.json")));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void A_file_this_user_cannot_remove_is_copied_once_and_does_not_come_back_after_it_is_cleared()
    {
        string pending = PutLegacy("pending-reboot.json", "restart at 03:00", DateTime.UtcNow.AddHours(-3));
        MakeUnremovable(pending);
        string copy = Path.Combine(_shared, "pending-reboot.json");

        Assert.Equal(1, Migrate());
        Assert.Equal("restart at 03:00", File.ReadAllText(copy));
        Assert.True(File.Exists(pending));
        Assert.Empty(Directory.GetFiles(_legacy, "*.migrating"));

        // The user cancels the restart: the next passes must not bring it back.
        File.Delete(copy);
        Assert.Equal(0, Migrate());
        Assert.Equal(0, Migrate());
        Assert.False(File.Exists(copy));
        Assert.Equal(Path.Combine(_legacy, "user"), AutomationStatePaths.ResolveDirectory(_shared, Path.Combine(_legacy, "user")));

        // The service or an elevated helper can remove the old file; it does not copy it again.
        MakeRemovable(pending);
        Assert.Equal(0, Migrate());
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(copy));
        Assert.Empty(Directory.GetFiles(_shared));
    }

    [Fact]
    public void A_file_this_user_cannot_remove_is_copied_again_when_an_old_agent_rewrites_it()
    {
        string queue = PutLegacy("applications-install-queue.json", "install at 21:00", DateTime.UtcNow.AddHours(-3));
        MakeUnremovable(queue);
        string copy = Path.Combine(_shared, "applications-install-queue.json");
        Assert.Equal(1, Migrate());

        Rewrite(queue, "install at 22:30");
        File.SetLastWriteTimeUtc(queue, DateTime.UtcNow.AddHours(1));

        Assert.Equal(1, Migrate());
        Assert.Equal("install at 22:30", File.ReadAllText(copy));
        Assert.Equal(0, Migrate());
    }

    [Fact]
    public void A_file_this_user_cannot_remove_does_not_replace_a_newer_one_in_the_new_folder()
    {
        string pending = PutLegacy("pending-reboot.json", "old", DateTime.UtcNow.AddHours(-3));
        MakeUnremovable(pending);
        PutShared("pending-reboot.json", "new", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(0, Migrate());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_shared, "pending-reboot.json")));

        MakeRemovable(pending);
        Assert.Equal(0, Migrate());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_shared, "pending-reboot.json")));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void A_move_interrupted_after_the_rename_is_finished_on_the_next_pass()
    {
        PutLegacy("pending-reboot.json.migrating", "restart");
        PutLegacy("fleet-policy.json.migrating", "not state");

        Assert.Equal(1, Migrate());

        Assert.Equal("restart", File.ReadAllText(Path.Combine(_shared, "pending-reboot.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "pending-reboot.json.migrating")));
        Assert.True(File.Exists(Path.Combine(_legacy, "fleet-policy.json.migrating")));
        Assert.False(File.Exists(Path.Combine(_shared, "fleet-policy.json")));
    }

    [Fact]
    public void Taking_back_the_grant_leaves_the_state_subfolder_and_every_other_rule_alone()
    {
        string fleet = PutLegacy("fleet-policy.json");
        string state = PutShared("pending-reboot.json");
        string otherFolder = Path.Combine(_legacy, "Other");
        string otherFile = Put(otherFolder, "notes.txt", "x", null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var folder = new DirectoryInfo(_legacy);
        var before = folder.GetAccessControl();
        before.AddAccessRule(new FileSystemAccessRule(
            authenticated, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        before.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(before);
        GrantTheWayBuildsAfter61Did(_legacy);
        Assert.True(UsersCanModify(new FileInfo(fleet).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(otherFile).GetAccessControl()));

        Assert.True(AutomationStatePaths.TryRevokeUsersModify(_legacy, _shared));

        Assert.False(UsersCanModify(folder.GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(fleet).GetAccessControl()));
        Assert.False(UsersCanModify(new DirectoryInfo(otherFolder).GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(otherFile).GetAccessControl()));
        Assert.True(UsersCanModify(new DirectoryInfo(_shared).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(state).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(PutShared("drivers-prompt.json")).GetAccessControl()));
        Assert.True(Allows(folder.GetAccessControl(), authenticated, FileSystemRights.Modify));
        Assert.True(Allows(folder.GetAccessControl(), users, FileSystemRights.ReadAndExecute));
        Assert.True(Allows(new FileInfo(fleet).GetAccessControl(), users, FileSystemRights.ReadAndExecute));

        Assert.True(AutomationStatePaths.TryRevokeUsersModify(_legacy, _shared));
    }

    [Fact]
    public void Taking_back_a_grant_that_was_never_made_changes_nothing()
    {
        PutLegacy("fleet-policy.json");
        string sddl = new DirectoryInfo(_legacy).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Assert.True(AutomationStatePaths.TryRevokeUsersModify(_legacy, _shared));
        Assert.True(AutomationStatePaths.TryRevokeUsersModify(Path.Combine(_legacy, "missing")));

        Assert.Equal(sddl, new DirectoryInfo(_legacy).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.False(Directory.Exists(_shared));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\MicroSystems\Update Express", @"C:\ProgramData\MicroSystems\Update Express\State")]
    [InlineData(@"c:\programdata\microsystems\update express\", @"C:\ProgramData\MicroSystems\Update Express\State")]
    [InlineData(@"C:\ProgramData\MicroSystems\Update Express\State", @"C:\ProgramData\MicroSystems\Update Express\State")]
    [InlineData(@"C:\Users\A\AppData\Local\MicroSystems\Update Express", @"C:\Users\A\AppData\Local\MicroSystems\Update Express")]
    public void A_helper_started_with_the_old_folder_uses_the_state_subfolder(string passed, string expected)
    {
        Assert.Equal(expected, AutomationStatePaths.RedirectLegacyShared(
            passed,
            @"C:\ProgramData\MicroSystems\Update Express",
            @"C:\ProgramData\MicroSystems\Update Express\State"));
    }

    [Fact]
    public void The_grant_is_refused_on_a_junction_and_its_target_is_untouched()
    {
        string target = Outside("target");
        string link = Junction(_shared, target);

        Assert.False(AutomationStatePaths.TryGrantUsersModify(link));

        Assert.False(UsersCanModify(new DirectoryInfo(target).GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(Path.Combine(target, "victim.txt")).GetAccessControl()));
    }

    [Fact]
    public void The_grant_is_refused_when_a_folder_on_the_way_is_a_junction()
    {
        string target = Outside("target");
        Directory.CreateDirectory(Path.Combine(target, "State"));
        string link = Junction(Path.Combine(_legacy, "Update Express"), target);
        string state = Path.Combine(link, "State");

        Assert.False(StateFolderAcl.TryGrantUsersModify(state, _legacy, out string failure));

        Assert.Equal("the path leads through a link", failure);
        Assert.False(UsersCanModify(new DirectoryInfo(Path.Combine(target, "State")).GetAccessControl()));
    }

    [Fact]
    public void The_grant_does_not_reach_through_a_junction_inside_the_folder()
    {
        string own = PutShared("pending-reboot.json");
        string target = Outside("target");
        Junction(Path.Combine(_shared, "planted"), target);

        Assert.True(AutomationStatePaths.TryGrantUsersModify(_shared));

        Assert.True(UsersCanModify(new FileInfo(own).GetAccessControl()));
        Assert.False(UsersCanModify(new DirectoryInfo(target).GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(Path.Combine(target, "victim.txt")).GetAccessControl()));
        Assert.False(UsersCanModify(new DirectoryInfo(Path.Combine(target, "sub")).GetAccessControl()));
        Assert.False(UsersCanModify(new FileInfo(Path.Combine(target, "sub", "deep.txt")).GetAccessControl()));
    }

    [Fact]
    public void Taking_back_the_grant_does_not_reach_through_a_junction()
    {
        PutLegacy("fleet-policy.json");
        string target = Outside("target");
        GrantTheWayBuildsAfter61Did(target);
        GrantTheWayBuildsAfter61Did(_legacy);
        Junction(Path.Combine(_legacy, "planted"), target);

        Assert.True(AutomationStatePaths.TryRevokeUsersModify(_legacy, _shared));

        Assert.False(UsersCanModify(new DirectoryInfo(_legacy).GetAccessControl()));
        Assert.True(UsersCanModify(new DirectoryInfo(target).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(Path.Combine(target, "victim.txt")).GetAccessControl()));
        Assert.True(UsersCanModify(new FileInfo(Path.Combine(target, "sub", "deep.txt")).GetAccessControl()));
    }

    [Fact]
    public void Nothing_moves_into_a_state_folder_that_is_a_junction()
    {
        string pending = PutLegacy("pending-reboot.json", "restart");
        string target = Outside("target");
        Junction(_shared, target);

        Assert.Equal(0, Migrate());

        Assert.Equal("restart", File.ReadAllText(pending));
        Assert.Equal(new[] { "victim.txt" }, Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void A_pass_is_left_to_the_process_that_is_already_moving_the_files()
    {
        string pending = PutLegacy("pending-reboot.json", "restart");
        Directory.CreateDirectory(_shared);
        string lockPath = Path.Combine(_shared, ".migration.lock");

        using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, Migrate());
            Assert.Equal("restart", File.ReadAllText(pending));
            Assert.Empty(Directory.GetFiles(_legacy, "*.migrating"));
            Assert.False(File.Exists(Path.Combine(_shared, "pending-reboot.json")));
        }

        // The lock file of a process that is gone does not hold anyone back.
        Assert.True(File.Exists(lockPath));
        Assert.Equal(1, Migrate());
        Assert.Equal(new[] { "pending-reboot.json" }, Directory.GetFiles(_shared).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_is_written_under_a_new_name_and_replaces_what_waits_at_the_target()
    {
        string target = PutShared("pending-reboot.json", "planted");
        File.SetAttributes(target, FileAttributes.Hidden);
        var written = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

        LinkSafeFile.Replace(target, output => output.Write("fresh"u8), written);

        Assert.Equal("fresh", File.ReadAllText(target));
        Assert.Equal(written, File.GetLastWriteTimeUtc(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
        Assert.Single(Directory.GetFiles(_shared));
    }

    [Fact]
    public void Reading_refuses_a_link_and_a_folder()
    {
        string target = Outside("target");
        string link = Junction(Path.Combine(_legacy, "pending-reboot.json"), target);

        // Windows itself refuses to open a folder, or a junction that is not followed, as a file.
        Assert.Throws<UnauthorizedAccessException>(() => LinkSafeFile.OpenRead(link).Dispose());
        Assert.Throws<UnauthorizedAccessException>(() => LinkSafeFile.OpenRead(target).Dispose());
        Assert.Throws<FileNotFoundException>(() => LinkSafeFile.OpenRead(Path.Combine(_legacy, "missing.json")).Dispose());
        Assert.Equal(0, Migrate());
    }

    [Fact]
    public void A_read_only_old_file_is_moved_too()
    {
        string queue = PutLegacy("drivers-install-queue.json", "queue");
        File.SetAttributes(queue, FileAttributes.ReadOnly);

        Assert.Equal(1, Migrate());

        Assert.False(File.Exists(queue));
        Assert.Empty(Directory.GetFiles(_legacy));
        Assert.Equal("queue", File.ReadAllText(Path.Combine(_shared, "drivers-install-queue.json")));
    }

    /// <summary>A folder outside the tested tree, the way a planted junction would point at one.</summary>
    private string Outside(string name)
    {
        string dir = Path.Combine(_outside, name);
        Put(dir, "victim.txt", "x", null);
        Put(Path.Combine(dir, "sub"), "deep.txt", "x", null);
        return dir;
    }

    private string Junction(string link, string target)
    {
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");
        _junctions.Add(link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        return link;
    }

    /// <summary>Exactly the command the builds between 0.7.2.61 and the State subfolder ran.</summary>
    private static void GrantTheWayBuildsAfter61Did(string dir) =>
        Run(Path.Combine(Environment.SystemDirectory, "icacls.exe"), $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q");

    private static void Run(string file, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static bool UsersCanModify(FileSystemSecurity security) =>
        Allows(security, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify);

    private static bool Allows(FileSystemSecurity security, SecurityIdentifier who, FileSystemRights rights)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(who) &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & rights) == rights)
                return true;
        }
        return false;
    }
}
