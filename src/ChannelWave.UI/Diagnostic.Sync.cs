using ChannelWave.Core;

namespace ChannelWave.UI;

public partial class Diagnostic
{
    private Task RefreshSource(Source source) => source.AccountId == Session.AccountId ? StartSync() : Task.CompletedTask;
}
