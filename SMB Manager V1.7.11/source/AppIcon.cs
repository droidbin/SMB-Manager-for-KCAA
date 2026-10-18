using System.Drawing;
using System.Reflection;

namespace SmbManagerAssets
{
    internal static class AppIcon
    {
        public static Icon Load()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SmbManager.AppIcon.ico"))
            using (var icon = new Icon(stream))
            {
                return (Icon)icon.Clone();
            }
        }
    }
}
