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


    /// things to change 
    /// go from tcpip to wireless debugging
    /// save debug port to config per device
    /// remove stupid Saveddevices from config and just save the last active deice name (removes the need for duplicates)
    /// first try mdns to find pottentialy save wether it supports it
    public partial class App : Application
    {
        public static readonly string CurrentVersion = "v1.2-beta21";
    }

}