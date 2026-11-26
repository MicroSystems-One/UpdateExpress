using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// Only an administrator or SYSTEM can create or change fleet-policy.json; a standard user can
/// still read it and write the files the signed-in user's agent writes.
/// <para>
/// Everything runs in a temp folder that has the rights ProgramData has. The PC's own
/// ProgramData folder is not touched. The test's account plays the administrator while the
/// folder is secured, with a made-up account standing in for the Administrators group; a
/// second pass without the test's account then leaves it the rights a standard user has, and
/// the checks are real file operations.
/// </para>
/// </summary>
public sealed class PolicyFolderGuardTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier StandInAdministrators = new("S-1-5-21-1004336348-1177238915-682003330-9001");
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-1004336348-1177238915-682003330-9002");
    private static readonly SecurityIdentifier ThirdUser = new("S-1-5-21-1004336348-1177238915-682003330-9003");

    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ue-policy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "ue-policy-outside-" + Guid.NewGuid().ToString("N"));
    private readonly string _vendor;
    private readonly string _folder;
    private readonly string _state;
    private readonly string _fleet;
    private readonly string _mirror;

    public PolicyFolderGuardTests()
    {
        _vendor = Path.Combine(_root, "MicroSystems");
        _folder = Path.Combine(_vendor, "Update Express");
        _state = Path.Combine(_folder, "State");
        _fleet = Path.Combine(_folder, "fleet-policy.json");
        _mirror = Path.Combine(_folder, "update-policy.json");

        // What C:\ProgramData has: everyone may add files and folders, and whoever adds a
        // folder gets full control of it.
        Directory.CreateDirectory(_root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl, ToChildren, PropagationFlags.InheritOnly, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.ReadAndExecute, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(security);
    }

    public void Dispose()
    {
        try { Free(new DirectoryInfo(_root)); } catch { /* temp folder; best effort */ }
        try { if (Directory.Exists(_outside)) Directory.Delete(_outside, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Give the test's account its rights back (it owns everything here), then delete.</summary>
    private static void Free(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            dir.Delete();
            return;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
        foreach (FileInfo file in dir.EnumerateFiles())
        {
            var open = new FileSecurity();
            open.SetAccessRuleProtection(true, false);
            open.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(open);
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }
        foreach (DirectoryInfo sub in dir.EnumerateDirectories())
            Free(sub);
        dir.Delete();
    }

    /// <summary>The test's account as the administrator who installs.</summary>
    private static PolicyFolderGuard.Identities Administrator(bool madeByAdministrator = true) => new(
        Me,
        new[] { StandInAdministrators, Me },
        Users,
        sid => madeByAdministrator && sid.Equals(Me));

    /// <summary>The same PC afterwards, where the test's account has what a standard user has.</summary>
    private static PolicyFolderGuard.Identities Settled => new(Me, new[] { StandInAdministrators }, Users, sid => sid.Equals(Me));

    private PolicyFolderGuard.Outcome Secure(PolicyFolderGuard.Identities ids)
    {
        PolicyFolderGuard.Outcome outcome = PolicyFolderGuard.Secure(_folder, _root, ids);
        Assert.True(outcome.Secured, outcome.Failure);
        return outcome;
    }

    /// <summary>Secure the folder, write the status files as the service would, then become a standard user.</summary>
    private void SecureThenBecomeStandardUser()
    {
        Secure(Administrator());
        PolicyFolderGuard.WriteUserFile(Path.Combine(_folder, "rmm-status.json"), "{\"a\":1}"u8.ToArray());
        PolicyFolderGuard.WriteUserFile(Path.Combine(_folder, "rmm-status.txt"), "ue.a=1"u8.ToArray());
        PolicyFolderGuard.Outcome settled = Secure(Settled);
        Assert.False(settled.StatusFilesMissing);
    }

    [Fact]
    public void MissingFolders_AreCreatedAndLocked()
    {
        PolicyFolderGuard.Outcome outcome = Secure(Administrator());

        DirectorySecurity folder = new DirectoryInfo(_folder).GetAccessControl();
        Assert.True(folder.AreAccessRulesProtected);
        Assert.Equal(Me, folder.GetOwner(typeof(SecurityIdentifier)));
        var rules = Rules(folder);
        Assert.Equal(3, rules.Count);
        Assert.All(rules, rule => Assert.Equal(ToChildren, rule.InheritanceFlags));
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(StandInAdministrators) && rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(Me) && rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(Users) &&
            rule.FileSystemRights == (FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));

        Assert.True(Allows(new DirectoryInfo(_state).GetAccessControl(), Users, FileSystemRights.Modify));
        Assert.Equal("{}", File.ReadAllText(_mirror));
        Assert.True(Allows(new FileInfo(_mirror).GetAccessControl(), Users, FileSystemRights.Write));
        Assert.False(Allows(new FileInfo(_mirror).GetAccessControl(), Users, FileSystemRights.Delete));
        // Not created empty: the RMM script would report an empty file as the status.
        Assert.True(outcome.StatusFilesMissing);
        Assert.False(File.Exists(Path.Combine(_folder, "rmm-status.json")));
    }

    [Fact]
    public void StandardUser_CannotCreateFleetPolicy_WhereNoneExists()
    {
        SecureThenBecomeStandardUser();

        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_fleet, "{\"SetupProfile\":\"mine\"}"));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(_folder, "anything.json"), "{}"));
        Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(_folder, "fleet-policy.json")));
        File.WriteAllText(Path.Combine(_state, "mine.json"), "{}");
        Assert.Throws<UnauthorizedAccessException>(() => File.Move(Path.Combine(_state, "mine.json"), _fleet));
        Assert.False(File.Exists(_fleet));
    }

    [Fact]
    public void StandardUser_ReadsFleetPolicy_ButCannotChangeIt()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{\"SetupProfile\":\"business\"}");
        SecureThenBecomeStandardUser();

        Assert.Equal("{\"SetupProfile\":\"business\"}", File.ReadAllText(_fleet));
        Assert.True(PolicyFolderGuard.TryReadAdministratorFile(_fleet, sid => sid.Equals(Me), out string content, out string refused), refused);
        Assert.Equal("{\"SetupProfile\":\"business\"}", content);

        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_fleet, "{}"));
        Assert.Throws<UnauthorizedAccessException>(() => File.AppendAllText(_fleet, " "));
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(_fleet));
        Assert.Throws<UnauthorizedAccessException>(() => File.Move(_fleet, _fleet + ".old"));
        Assert.Throws<UnauthorizedAccessException>(() => File.Move(_mirror, _fleet, overwrite: true));
        Assert.Throws<UnauthorizedAccessException>(() => File.SetAttributes(_fleet, FileAttributes.Hidden));
        Assert.Equal("{\"SetupProfile\":\"business\"}", File.ReadAllText(_fleet));
    }

    [Fact]
    public void FilesTheUsersAgentWrites_StayWritableInPlace_ButCannotBeRemoved()
    {
        SecureThenBecomeStandardUser();

        foreach (string name in PolicyFolderGuard.UserWrittenFileNames)
        {
            string path = Path.Combine(_folder, name);
            PolicyFolderGuard.WriteUserFile(path, Encoding.UTF8.GetBytes("a longer text than what was there before"));
            Assert.Equal("a longer text than what was there before", File.ReadAllText(path));
            PolicyFolderGuard.WriteUserFile(path, "short"u8.ToArray());
            Assert.Equal("short", File.ReadAllText(path));

            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));
            Assert.Throws<UnauthorizedAccessException>(() => File.Move(path, path + ".old"));
            Assert.Equal("short", File.ReadAllText(path));
        }

        // The way versions up to 0.7.2.61 write the policy mirror.
        File.WriteAllText(_mirror, "{\"SetupCompleted\":true}");
        Assert.Equal("{\"SetupCompleted\":true}", File.ReadAllText(_mirror));
    }

    [Fact]
    public void State_StaysWritableForUsers_AndOldStateStillArrivesThere()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "pending-reboot.json"), "{\"due\":true}");
        SecureThenBecomeStandardUser();

        string own = Path.Combine(_state, "windows-automation-status.json");
        File.WriteAllText(own, "{}");
        File.Move(own, own + ".bak");
        File.Delete(own + ".bak");

        // The user's agent can no longer move the old file out of the locked folder; it copies.
        AutomationStatePaths.MigrateLegacyState(_folder, _state);
        Assert.Equal("{\"due\":true}", File.ReadAllText(Path.Combine(_state, "pending-reboot.json")));
    }

    [Fact]
    public void FleetPolicyAStandardUserPlanted_IsSetAside_AndNeverRead()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{\"SetupProfile\":\"planted\"}");

        // Before any elevated process has run: the reader already refuses it.
        Assert.False(PolicyFolderGuard.TryReadAdministratorFile(_fleet, _ => false, out string content, out string refused));
        Assert.Equal("", content);
        Assert.Contains("not made by an administrator", refused);

        PolicyFolderGuard.Outcome outcome = Secure(Administrator(madeByAdministrator: false));

        Assert.False(File.Exists(_fleet));
        Assert.Equal("{\"SetupProfile\":\"planted\"}", File.ReadAllText(_fleet + ".rejected"));
        Assert.Contains(outcome.Changes, change => change.Contains("set aside"));

        // A second planted file replaces the first one that was set aside.
        File.WriteAllText(_fleet, "{\"SetupProfile\":\"again\"}");
        Secure(Administrator(madeByAdministrator: false));
        Assert.False(File.Exists(_fleet));
        Assert.Equal("{\"SetupProfile\":\"again\"}", File.ReadAllText(_fleet + ".rejected"));
    }

    [Fact]
    public void JunctionAtTheFleetPolicyName_IsSetAside_NotFollowed()
    {
        Directory.CreateDirectory(_folder);
        string target = Outside("{\"SetupProfile\":\"elsewhere\"}");
        Junction(_fleet, _outside);
        string before = Sddl(_outside);

        Assert.False(PolicyFolderGuard.TryReadAdministratorFile(_fleet, _ => true, out _, out string refused));
        Assert.Contains("not a regular file", refused);
        Secure(Administrator());

        Assert.False(File.Exists(_fleet) || Directory.Exists(_fleet));
        Assert.Equal("{\"SetupProfile\":\"elsewhere\"}", File.ReadAllText(target));
        Assert.Equal(before, Sddl(_outside));
    }

    [Fact]
    public void FleetPolicyThatIsASecondNameOfAUserWritableFile_IsSetAside()
    {
        Secure(Administrator());
        string status = Path.Combine(_folder, "rmm-status.json");
        PolicyFolderGuard.WriteUserFile(status, "{\"SetupProfile\":\"through the status file\"}"u8.ToArray());
        Run("cmd.exe", $"/c mklink /H \"{_fleet}\" \"{status}\"");

        Assert.False(PolicyFolderGuard.TryReadAdministratorFile(_fleet, _ => true, out _, out string refused));
        Assert.Contains("second name", refused);

        PolicyFolderGuard.Outcome outcome = Secure(Administrator());
        Secure(Settled);

        Assert.False(File.Exists(_fleet));
        // The status file went too: it was one file with what was set aside. The service writes a new one.
        Assert.False(File.Exists(status));
        Assert.True(outcome.StatusFilesMissing);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_fleet, "{}"));
    }

    [Fact]
    public void UserWrittenFileThatIsASecondNameOfAnotherFile_IsReplaced_AndTheOtherFileGainsNoRights()
    {
        Directory.CreateDirectory(_folder);
        string other = Outside("{\"other\":true}");
        Run("cmd.exe", $"/c mklink /H \"{_mirror}\" \"{other}\"");
        string[] before = Grants(other);

        PolicyFolderGuard.Outcome outcome = Secure(Administrator());

        Assert.Equal("{}", File.ReadAllText(_mirror));
        Assert.Equal("{\"other\":true}", File.ReadAllText(other));
        // Who may do what with the other file is as before, and it kept its owner.
        Assert.Equal(before, Grants(other));
        Assert.Contains(outcome.Changes, change => change.Contains("second name"));
    }

    [Fact]
    public void FleetPolicyInUseByAnotherProcess_IsLeftInPlace()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{\"SetupProfile\":\"business\"}");
        using (new FileStream(_fleet, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            PolicyFolderGuard.Outcome busy = PolicyFolderGuard.Secure(_folder, _root, Administrator());
            Assert.DoesNotContain(busy.Changes, change => change.Contains("set aside") || change.Contains("removed"));
            Assert.True(File.Exists(_fleet));
        }

        Secure(Administrator());
        Assert.Equal("{\"SetupProfile\":\"business\"}", File.ReadAllText(_fleet));
        Assert.False(File.Exists(_fleet + ".rejected"));
    }

    [Fact]
    public void FleetPolicyAnAdministratorMade_LosesRightsAUserWasGivenOnIt()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{\"SetupProfile\":\"business\"}");
        var file = new FileInfo(_fleet);
        FileSecurity loose = file.GetAccessControl();
        loose.SetAccessRuleProtection(true, true);
        loose.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Allow));
        file.SetAccessControl(loose);

        Secure(Administrator());
        Secure(Settled);

        FileSecurity after = new FileInfo(_fleet).GetAccessControl();
        Assert.False(after.AreAccessRulesProtected);
        Assert.Empty(after.GetAccessRules(true, false, typeof(SecurityIdentifier)));
        Assert.False(Allows(after, Users, FileSystemRights.Write));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_fleet, "{}"));
        Assert.Equal("{\"SetupProfile\":\"business\"}", File.ReadAllText(_fleet));
    }

    [Fact]
    public void VendorFolder_OrdinaryAccountsLoseControlOfIt_ButKeepTheirRightsBelow()
    {
        // Created elsewhere and moved in: it arrives with the full control its creator had,
        // as an inherited rule. That is how a folder a standard user created looks.
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var parent = new DirectoryInfo(elsewhere);
        DirectorySecurity creator = parent.GetAccessControl();
        creator.AddAccessRule(new FileSystemAccessRule(OtherUser, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        parent.SetAccessControl(creator);
        var made = new DirectoryInfo(Path.Combine(elsewhere, "MicroSystems"));
        made.Create();
        DirectorySecurity given = made.GetAccessControl();
        given.AddAccessRule(new FileSystemAccessRule(ThirdUser, FileSystemRights.Modify, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        made.SetAccessControl(given);
        Directory.Move(made.FullName, _vendor);
        Assert.True(Allows(new DirectoryInfo(_vendor).GetAccessControl(), OtherUser, FileSystemRights.FullControl));
        Assert.True(Allows(new DirectoryInfo(_vendor).GetAccessControl(), ThirdUser, FileSystemRights.Modify));

        PolicyFolderGuard.Outcome outcome = Secure(Administrator());

        DirectorySecurity after = new DirectoryInfo(_vendor).GetAccessControl();
        Assert.False(after.AreAccessRulesProtected);
        Assert.DoesNotContain(Rules(after), rule => rule.IdentityReference.Equals(OtherUser));
        Assert.False(Allows(after, ThirdUser, FileSystemRights.Delete));
        Assert.False(Allows(after, ThirdUser, FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.True(Allows(after, ThirdUser, FileSystemRights.ReadAndExecute | FileSystemRights.Write));
        Assert.Contains(Rules(after), rule =>
            rule.IdentityReference.Equals(ThirdUser) &&
            (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 &&
            (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        Assert.Contains(outcome.Changes, change => change.Contains("control of"));
        // The policy folder below is closed for both.
        Assert.DoesNotContain(Rules(new DirectoryInfo(_folder).GetAccessControl()), rule =>
            rule.IdentityReference.Equals(OtherUser) || rule.IdentityReference.Equals(ThirdUser));
    }

    [Fact]
    public void VendorFolder_FullControlItsCreatorGotByCreatingIt_Goes()
    {
        // The plain case: nothing but the rule Windows gives the creator of a folder.
        string elsewhere = Path.Combine(_root, "elsewhere");
        var parent = new DirectoryInfo(elsewhere);
        parent.Create();
        DirectorySecurity creator = parent.GetAccessControl();
        creator.AddAccessRule(new FileSystemAccessRule(OtherUser, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        parent.SetAccessControl(creator);
        Directory.CreateDirectory(Path.Combine(elsewhere, "MicroSystems", "Update Express"));
        Directory.Move(Path.Combine(elsewhere, "MicroSystems"), _vendor);
        Assert.True(Allows(new DirectoryInfo(_vendor).GetAccessControl(), OtherUser, FileSystemRights.FullControl));
        Assert.True(Allows(new DirectoryInfo(_folder).GetAccessControl(), OtherUser, FileSystemRights.FullControl));

        Secure(Administrator());

        Assert.DoesNotContain(Rules(new DirectoryInfo(_vendor).GetAccessControl()), rule => rule.IdentityReference.Equals(OtherUser));
        Assert.DoesNotContain(Rules(new DirectoryInfo(_folder).GetAccessControl()), rule => rule.IdentityReference.Equals(OtherUser));
        Assert.DoesNotContain(Rules(new DirectoryInfo(_state).GetAccessControl()), rule => rule.IdentityReference.Equals(OtherUser));
        Assert.DoesNotContain(Rules(new FileInfo(_mirror).GetAccessControl()), rule => rule.IdentityReference.Equals(OtherUser));
    }

    [Fact]
    public void PolicyFolderWhoseOwnerRewroteTheRights_GetsThemReplaced()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{}");
        var rewritten = new DirectorySecurity();
        rewritten.SetAccessRuleProtection(true, false);
        rewritten.AddAccessRule(new FileSystemAccessRule(OtherUser, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        rewritten.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, ToChildren, PropagationFlags.None, AccessControlType.Allow));
        rewritten.AddAccessRule(new FileSystemAccessRule(StandInAdministrators, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Deny));
        new DirectoryInfo(_folder).SetAccessControl(rewritten);

        Secure(Administrator());
        Secure(Settled);

        var rules = Rules(new DirectoryInfo(_folder).GetAccessControl());
        Assert.Equal(2, rules.Count);
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(OtherUser));
        var file = Rules(new FileInfo(_fleet).GetAccessControl());
        Assert.Equal(2, file.Count);
        Assert.All(file, rule => Assert.True(rule.IsInherited));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_fleet, "{\"SetupProfile\":\"mine\"}"));
        Assert.Equal("{}", File.ReadAllText(_fleet));
    }

    [Fact]
    public void PolicyFolderPlantedAsAJunction_IsReplacedByARealFolder()
    {
        Directory.CreateDirectory(_vendor);
        string target = Outside("{\"SetupProfile\":\"elsewhere\"}");
        Junction(_folder, _outside);
        string before = Sddl(_outside);

        PolicyFolderGuard.Outcome outcome = Secure(Administrator(madeByAdministrator: false));

        Assert.Equal((FileAttributes)0, File.GetAttributes(_folder) & FileAttributes.ReparsePoint);
        Assert.False(File.Exists(_fleet));
        Assert.True(new DirectoryInfo(_folder).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal("{\"SetupProfile\":\"elsewhere\"}", File.ReadAllText(target));
        Assert.Equal(before, Sddl(_outside));
        Assert.Contains(outcome.Changes, change => change.Contains("removed a link"));
    }

    [Fact]
    public void JunctionsAtTheStateFolderAndAtAUserFile_AreRemoved_NotFollowed()
    {
        Directory.CreateDirectory(_folder);
        string target = Outside("kept");
        Junction(_state, _outside);
        Junction(_mirror, _outside);
        string before = Sddl(_outside);

        Secure(Administrator(madeByAdministrator: false));

        Assert.Equal((FileAttributes)0, File.GetAttributes(_state) & FileAttributes.ReparsePoint);
        Assert.Equal((FileAttributes)0, File.GetAttributes(_mirror) & (FileAttributes.ReparsePoint | FileAttributes.Directory));
        Assert.Equal("{}", File.ReadAllText(_mirror));
        Assert.Equal("kept", File.ReadAllText(target));
        Assert.Equal(before, Sddl(_outside));
    }

    [Fact]
    public void SecondPass_ChangesNothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_fleet, "{}");
        Secure(Administrator());
        PolicyFolderGuard.WriteUserFile(Path.Combine(_folder, "rmm-status.json"), "{}"u8.ToArray());
        PolicyFolderGuard.WriteUserFile(Path.Combine(_folder, "rmm-status.txt"), "x"u8.ToArray());
        Secure(Administrator());
        string[] paths = { _vendor, _folder, _state, _fleet, _mirror, Path.Combine(_folder, "rmm-status.json") };
        string[] before = paths.Select(Sddl).ToArray();

        PolicyFolderGuard.Outcome again = Secure(Administrator());

        Assert.Empty(again.Changes);
        Assert.Equal(before, paths.Select(Sddl).ToArray());
    }

    [Fact]
    public void WriteUserFile_CreatesAMissingFile_AndDoesNotWriteThroughALink()
    {
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        string file = Path.Combine(plain, "rmm-status.json");
        PolicyFolderGuard.WriteUserFile(file, "first"u8.ToArray());
        Assert.Equal("first", File.ReadAllText(file));

        string target = Outside("kept");
        string link = Path.Combine(plain, "rmm-status.txt");
        Junction(link, _outside);
        Assert.ThrowsAny<Exception>(() => PolicyFolderGuard.WriteUserFile(link, "second"u8.ToArray()));
        Assert.Equal("kept", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_outside));
        Assert.Single(Directory.GetFiles(plain));
    }

    [Fact]
    public void Reader_RefusesWhatIsMissingOrNotAFile()
    {
        Directory.CreateDirectory(_fleet);
        Assert.False(PolicyFolderGuard.TryReadAdministratorFile(_fleet, _ => true, out _, out string folder));
        Assert.Contains("not a regular file", folder);
        Assert.False(PolicyFolderGuard.TryReadAdministratorFile(_fleet + ".none", _ => true, out _, out string missing));
        Assert.Contains("cannot be opened", missing);
    }

    [Fact]
    public void LocalAdministrators_AreSystemTheGroupAndItsMembers_NotEveryone()
    {
        Assert.True(LocalAdministrators.IsAdministrator(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(LocalAdministrators.IsAdministrator(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(LocalAdministrators.IsAdministrator(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.False(LocalAdministrators.IsAdministrator(Users));
        Assert.False(LocalAdministrators.IsAdministrator(OtherUser));

        // The built-in Administrator account is a member on every PC. Its identifier is known
        // only when the test's account is a local one.
        if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
            Me.AccountDomainSid is SecurityIdentifier machine)
            Assert.True(LocalAdministrators.IsAdministrator(new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, machine)));
    }

    [Fact]
    public void TokenPrivilege_EnablesWhatTheTokenHolds_AndReportsWhatItDoesNot()
    {
        // On for every process; holding it must not switch it off afterwards.
        using (TokenPrivilege.Hold("SeChangeNotifyPrivilege")) { }
        Assert.True(TokenPrivilege.TryEnable("SeChangeNotifyPrivilege", out bool wasDisabled));
        Assert.False(wasDisabled);

        // Every user holds this one, switched off.
        using (TokenPrivilege.Hold("SeIncreaseWorkingSetPrivilege"))
        {
            Assert.True(TokenPrivilege.TryEnable("SeIncreaseWorkingSetPrivilege", out wasDisabled));
            Assert.False(wasDisabled);
        }
        using (TokenPrivilege.Hold("SeIncreaseWorkingSetPrivilege"))
        {
            // Back off after the first hold, so this one switched it on again.
        }
        Assert.True(TokenPrivilege.TryEnable("SeIncreaseWorkingSetPrivilege", out wasDisabled));
        Assert.True(wasDisabled);
        using (TokenPrivilege.Hold("SeIncreaseWorkingSetPrivilege")) { }

        Assert.False(TokenPrivilege.TryEnable("SeNoSuchPrivilege", out _));
        // Held only when elevated; either way it must not throw.
        using (TokenPrivilege.Hold(TokenPrivilege.OverrideRights)) { }
    }

    private string Outside(string content)
    {
        Directory.CreateDirectory(_outside);
        string path = Path.Combine(_outside, "target.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Junction(string link, string target) =>
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");

    private static void Run(string file, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, file),
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

    private static string Sddl(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
    }

    /// <summary>Owner and who is allowed what, leaving out whether a rule is inherited.</summary>
    private static string[] Grants(string path)
    {
        FileSecurity security = new FileInfo(path).GetAccessControl();
        return Rules(security)
            .Select(rule => $"{rule.AccessControlType} {rule.IdentityReference.Value} {(int)rule.FileSystemRights:X}")
            .Append("owner " + security.GetOwner(typeof(SecurityIdentifier))!.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static bool Allows(FileSystemSecurity security, SecurityIdentifier who, FileSystemRights rights) =>
        Rules(security).Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Equals(who) &&
            (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
            (rule.FileSystemRights & rights) == rights);
}
