using Godot;
using Keylight;

public partial class LicenseGate : Node
{
    public override void _Ready()
    {
        var config = KeylightConfig.Builder("your-tenant", "your-game", "sdk_live_demo").Build();
        var client = new KeylightClient(config);
        GD.Print(client.State);
    }
}
