using System;
using Newtonsoft.Json;

[Serializable]
public class KtaneModule
{
    [JsonProperty("ModuleID")]
    public string ModuleId { get; private set; }

    [JsonProperty("Name")]
    public string Name { get; private set; }

    [JsonProperty("X")]
    public int IconX { get; private set; }

    [JsonProperty("Y")]
    public int IconY { get; private set; }
}

[Serializable]
public class KtaneModuleResult
{
    [JsonProperty("KtaneModules")]
    public KtaneModule[] KtaneModules { get; private set; }
}