using System.Reflection;

namespace Screenline
{
    internal static class AppVersion
    {
        public static string Current
        {
            get
            {
                var attribute = (AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(
                    typeof(AppVersion).Assembly, typeof(AssemblyInformationalVersionAttribute));
                return attribute == null || string.IsNullOrWhiteSpace(attribute.InformationalVersion)
                    ? "0.1.5" : attribute.InformationalVersion;
            }
        }
    }
}
