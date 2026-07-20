namespace FsmMaster;

// A single loader-managed setting value. The Silksong loader backs this with a BepInEx
// ConfigEntry<T>; other loaders back it with their own settings storage.
public interface IFsmConfigValue<T>
{
    T Value { get; set; }
}
