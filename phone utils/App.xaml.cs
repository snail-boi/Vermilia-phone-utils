using System.IO;
using System.Windows;

namespace phone_utils
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>

    // i will be moving out of beta soon so this versioning scheme will change, just a heads up
    // also means we will need an extra version in between to go from beta to stable
    // also i will almost review the entire codebase to optimize it and make it cleaner
    // also the app will get a new name
    //path's will also change to reflect the new name
    // configs will now be found in snail-boi/new-app-name/ to provide compatibility with future apps and projects
    // also the repo name will change
    public partial class App : Application
    {
        public static readonly string CurrentVersion = "v1.3-beta3";
        public static readonly bool ScarletStarfallExists = File.Exists("C:\\Program Files (x86)\\Scarlet Phone Shortcuts\\Scarlet Phone Shortcuts.exe");
        public static readonly bool CrimsonDawnExists = File.Exists("C:\\Program Files (x86)\\Crimson Phone Presence\\Crimson Phone Presence.exe");
    }

}