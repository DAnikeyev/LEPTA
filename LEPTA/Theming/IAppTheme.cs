using System.Windows;

namespace LEPTA.Theming;

internal interface IAppTheme
{
    string Name { get; }
    bool IsDark { get; }
    ResourceDictionary CreateResources();
}

