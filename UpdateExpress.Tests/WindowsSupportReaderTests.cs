using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using UpdateExpress.Services;
using Xunit;
using OsFacts = UpdateExpress.Services.WindowsSupportReader.OsFacts;

namespace UpdateExpress.Tests;

public class WindowsSupportReaderTests
{
    private static readonly DateTime Today = new(2026, 11, 26);

    private static OsFacts Server(int build, string editionId, string displayVersion, string releaseId, string productName, int productType = 3) =>
        new(build, productType, editionId, displayVersion, releaseId, productName);

    [Theory]
    [InlineData(17763, "ServerStandard", "", "1809", "Windows Server 2019 Standard", "Windows Server 2019 Standard", "1809", "09 January 2029")]
    [InlineData(20348, "ServerStandard", "21H2", "2009", "Windows Server 2022 Standard", "Windows Server 2022 Standard", "21H2", "14 October 2031")]
    [InlineData(20348, "ServerDatacenter", "21H2", "2009", "Windows Server 2022 Datacenter", "Windows Server 2022 Datacenter", "21H2", "14 October 2031")]
    [InlineData(14393, "ServerStandard", "", "1607", "Windows Server 2016 Standard", "Windows Server 2016 Standard", "1607", "12 January 2027")]
    [InlineData(26100, "ServerStandard", "24H2", "2009", "Windows Server 2025 Standard", "Windows Server 2025 Standard", "24H2", "14 November 2034")]
    [InlineData(17763, "ServerStandardEval", "", "1809", "Windows Server 2019 Standard Evaluation", "Windows Server 2019 Standard Evaluation", "1809", "09 January 2029")]
    public void Server_IsNamedByItsRelease_AndJudgedByTheServerLifecycle(
        int build, string editionId, string displayVersion, string releaseId, string productName,
        string expectedName, string expectedVersion, string expectedEnd)
    {
        var result = WindowsSupportReader.Describe(Server(build, editionId, displayVersion, releaseId, productName), Today);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedVersion, result.Version);
        Assert.Equal("supported", result.Status);
        Assert.Contains($"Build {build}", result.Details);
        Assert.Contains(expectedEnd, result.Details);
        Assert.Equal(WindowsSupportReader.ServerReferenceUrl, result.ReferenceUrl);
        AssertNoClientWording(result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Server2019And2022_AreNotWindows10_AndNotAtEndOfSupport(int productType)
    {
        foreach (var facts in new[]
                 {
                     Server(17763, "ServerStandard", "", "1809", "Windows Server 2019 Standard", productType),
                     Server(20348, "ServerStandard", "21H2", "2009", "Windows Server 2022 Standard", productType)
                 })
        {
            var result = WindowsSupportReader.Describe(facts, Today);

            Assert.StartsWith("Windows Server 20", result.Name);
            Assert.NotEqual("expired", result.Status);
            AssertNoClientWording(result);
        }
    }

    [Fact]
    public void Server_IsExpiredOnlyAfterItsOwnLastDay()
    {
        var server2016 = Server(14393, "ServerStandard", "", "1607", "Windows Server 2016 Standard");

        Assert.Equal("supported", WindowsSupportReader.Describe(server2016, new DateTime(2027, 1, 12)).Status);
        var expired = WindowsSupportReader.Describe(server2016, new DateTime(2027, 1, 13));
        Assert.Equal("expired", expired.Status);
        Assert.Contains("Windows Server 2016", expired.Details);
        Assert.Contains("ended on 12 January 2027", expired.Details);
        Assert.Equal(WindowsSupportReader.ServerReferenceUrl, expired.ReferenceUrl);
        AssertNoClientWording(expired);

        var server2022 = Server(20348, "ServerStandard", "21H2", "2009", "Windows Server 2022 Standard");
        Assert.Equal("supported", WindowsSupportReader.Describe(server2022, new DateTime(2031, 10, 14)).Status);
        Assert.Equal("expired", WindowsSupportReader.Describe(server2022, new DateTime(2031, 10, 15)).Status);
    }

    [Theory]
    [InlineData(25398, "ServerDatacenter", "23H2", "Windows Server Datacenter", "Windows Server Datacenter")]
    [InlineData(9600, "ServerStandard", "", "Windows Server 2012 R2 Standard", "Windows Server 2012 R2 Standard")]
    [InlineData(19042, "ServerStandard", "20H2", "", "Windows Server Standard")]
    [InlineData(19042, "ServerStandard", "20H2", "Windows 10 Pro", "Windows Server Standard")]
    public void ServerReleaseOutsideTheTable_KeepsAServerName_AndClaimsNothing(
        int build, string editionId, string displayVersion, string productName, string expectedName)
    {
        var result = WindowsSupportReader.Describe(Server(build, editionId, displayVersion, "", productName), Today);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal("unknown", result.Status);
        Assert.Equal(WindowsSupportReader.ServerReferenceUrl, result.ReferenceUrl);
        AssertNoClientWording(result);
    }

    private static void AssertNoClientWording(WindowsSupportReader.Result result)
    {
        foreach (string text in new[] { result.Name, result.Details })
        {
            Assert.DoesNotContain("Windows 10", text);
            Assert.DoesNotContain("Windows 11", text);
            Assert.DoesNotContain("14 October 2025", text);
        }
    }

    [Fact]
    public void Client_IsDescribedAsBefore()
    {
        var windows10 = WindowsSupportReader.Describe(new OsFacts(19045, 1, "Professional", "22H2", "2009", "Windows 10 Pro"), Today);
        Assert.Equal("Windows 10 Pro 22H2", windows10.Name);
        Assert.Equal("expired", windows10.Status);
        Assert.Contains("Standard Windows 10 support ended on 14 October 2025", windows10.Details);
        Assert.Equal(WindowsSupportReader.ClientReferenceUrl, windows10.ReferenceUrl);

        var windows11 = WindowsSupportReader.Describe(new OsFacts(26200, 1, "Professional", "25H2", "2009", "Windows 10 Pro"), Today);
        Assert.Equal("Windows 11 Pro 25H2", windows11.Name);
        Assert.Equal("supported", windows11.Status);
        Assert.Contains("scheduled through 12 October 2027.", windows11.Details);
        Assert.Equal(WindowsSupportReader.ClientReferenceUrl, windows11.ReferenceUrl);

        var ltsc = WindowsSupportReader.Describe(new OsFacts(19044, 1, "EnterpriseS", "21H2", "2009", "Windows 10 Enterprise LTSC 2021"), Today);
        Assert.Equal("Windows 10 Enterprise LTSC 21H2", ltsc.Name);
        Assert.Equal("unknown", ltsc.Status);

        var home = WindowsSupportReader.Describe(new OsFacts(22631, 1, "Core", "23H2", "2009", "Windows 10 Home"), Today);
        Assert.Equal("Windows 11 Home 23H2", home.Name);
        Assert.Equal("expired", home.Status);
    }

    [Fact]
    public void Client_EndDateIsEnglishLikeItsSentence_WhateverTheRegionalSettingsAre()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("nb-NO");

            var supported = WindowsSupportReader.Describe(new OsFacts(26200, 1, "Professional", "25H2", "2009", "Windows 10 Pro"), Today);
            var expired = WindowsSupportReader.Describe(new OsFacts(22631, 1, "Core", "23H2", "2009", "Windows 10 Home"), Today);

            Assert.Contains("scheduled through 12 October 2027.", supported.Details);
            Assert.Contains("ended on 11 November 2025.", expired.Details);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void ProductTypeThatWasNotRead_IsNotTakenForAServer()
    {
        var result = WindowsSupportReader.Describe(new OsFacts(19045, null, "Professional", "22H2", "2009", "Windows 10 Pro"), Today);

        Assert.Equal("Windows 10 Pro 22H2", result.Name);
        Assert.Equal("expired", result.Status);
    }

    [Fact]
    public void ThisMachine_IsReadWithoutFailing()
    {
        var result = WindowsSupportReader.Read();

        Assert.DoesNotContain("unknown)", result.Name);
        Assert.StartsWith(WindowsProductType.IsServerOs ? "Windows Server" : "Windows 1", result.Name);
        Assert.Equal(
            WindowsProductType.IsServerOs ? WindowsSupportReader.ServerReferenceUrl : WindowsSupportReader.ClientReferenceUrl,
            result.ReferenceUrl);
    }

    [Fact]
    public void SupportDialog_LinksToTheServerPageForAServer_AndToTheClientPageOtherwise()
    {
        Assert.Equal(OsSupportInfoDialog.ReferenceUrl, WindowsSupportReader.ClientReferenceUrl);

        RunSta(() =>
        {
            var server = new OsSupportInfoDialog("details", norwegian: false, WindowsSupportReader.ServerReferenceUrl);
            Assert.Equal(WindowsSupportReader.ServerReferenceUrl, server.ReferenceLinkText.Text);
            Assert.Equal(new Uri(WindowsSupportReader.ServerReferenceUrl), server.ReferenceLink.NavigateUri);

            var client = new OsSupportInfoDialog("details", norwegian: false);
            Assert.Equal(WindowsSupportReader.ClientReferenceUrl, client.ReferenceLinkText.Text);
            Assert.Equal(new Uri(WindowsSupportReader.ClientReferenceUrl), client.ReferenceLink.NavigateUri);
        });
    }

    private static void RunSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
