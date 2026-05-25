using LEPTA.Shared.Models;

namespace LEPTA.vLLM.Models;

public sealed record LeptaPanelRequest(string Name, string CustomInstruction, string Format = LeptaPanelFormats.Markdown);

public sealed record LeptaPanelResponse(
	string Name,
	string Text,
	string? Error = null,
	int EstimatedVisibleTokenCount = 0,
	TimeSpan? GenerationDuration = null);
