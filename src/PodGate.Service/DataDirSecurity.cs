using System.Security.AccessControl;
using System.Security.Principal;
using PodGate.Core;

namespace PodGate.Service;

/// <summary>
/// %ProgramData% grants ordinary users "create files" by default, and a file a user creates is theirs to
/// rewrite. config.json decides which device an elevated service acts on, so the folder is reset to
/// SYSTEM and Administrators full control, Users read, with inheritance cut, and every file in it is made
/// to inherit that again.
/// </summary>
public static class DataDirSecurity
{
    public static void Apply()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var directory = new DirectorySecurity();
            directory.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            directory.SetOwner(admins);
            directory.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            directory.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            directory.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Paths.DataDir).SetAccessControl(directory);

            foreach (string file in Directory.EnumerateFiles(Paths.DataDir, "*", SearchOption.AllDirectories))
            {
                var security = new FileSecurity();
                security.SetOwner(admins);
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(file).SetAccessControl(security);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // Running unelevated from a console during development: nothing to secure there.
            Console.Error.WriteLine($"could not secure {Paths.DataDir}: {ex.Message}");
        }
    }
}
