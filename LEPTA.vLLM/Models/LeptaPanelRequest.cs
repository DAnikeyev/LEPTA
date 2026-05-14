namespace LEPTA.vLLM.Models;

public sealed record LeptaPanelRequest(string Name, string CustomInstruction);

public sealed record LeptaPanelResponse(string Name, string Text, string? Error = null);
