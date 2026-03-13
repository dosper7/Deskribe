namespace Deskribe.Sdk;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DeskribePluginAttribute : Attribute
{
    public string Name { get; }

    public DeskribePluginAttribute(string name)
    {
        Name = name;
    }
}
