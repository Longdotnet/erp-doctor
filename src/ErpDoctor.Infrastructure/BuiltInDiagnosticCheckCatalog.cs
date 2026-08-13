using ErpDoctor.Core;
using ErpDoctor.Infrastructure.HttpDiagnostics;
using ErpDoctor.Infrastructure.IisDiagnostics;
using ErpDoctor.Infrastructure.NetworkDiagnostics;
using ErpDoctor.Infrastructure.SqlServerDiagnostics;
using ErpDoctor.Infrastructure.SystemDiagnostics;
using ErpDoctor.Infrastructure.WindowsEventDiagnostics;

namespace ErpDoctor.Infrastructure;

public static class BuiltInDiagnosticCheckCatalog
{
    public static IReadOnlyList<IDiagnosticCheck> Create(ErpDoctorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var checks = new List<IDiagnosticCheck>
        {
            new DotNetRuntimeCheck(),
            new MemoryCheck(),
            new CpuUtilizationCheck(),
            new LoadAverageCheck(),
            new TopProcessesCheck(),
            new SqlConnectivityCheck(),
            new SqlDatabaseSizeCheck(),
            new SqlLargestTablesCheck(),
            new SqlBlockingCheck(),
            new SqlLongRunningRequestsCheck()
        };

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            checks.Add(new DiskSpaceCheck(drive));
        }

        foreach (var endpoint in options.Http.Endpoints)
        {
            checks.Add(new HttpEndpointCheck(endpoint));
        }

        foreach (var target in options.Network.Targets)
        {
            checks.Add(new DnsResolutionCheck(target));
            checks.Add(new TcpConnectivityCheck(target));
        }

        foreach (var appPool in options.Iis.AppPools)
        {
            checks.Add(new IisAppPoolCheck(appPool));
        }

        foreach (var site in options.Iis.Sites)
        {
            checks.Add(new IisSiteCheck(site));
        }

        foreach (var eventQuery in options.WindowsEventLog.Queries)
        {
            checks.Add(new WindowsEventLogCheck(eventQuery));
        }

        return checks;
    }
}
