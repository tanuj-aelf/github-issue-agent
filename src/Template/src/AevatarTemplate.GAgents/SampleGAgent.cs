using Aevatar.Core;
using Aevatar.Core.Abstractions;

namespace AevatarTemplate.GAgents;

[GenerateSerializer]
public class SampleGAgentState : StateBase;

[GenerateSerializer]
public class SampleStateLogEvent : StateLogEventBase<SampleStateLogEvent>;

[GAgent]
public class SampleGAgent : GAgentBase<SampleGAgentState, SampleStateLogEvent>
{
    public override Task<string> GetDescriptionAsync()
    {
        return Task.FromResult("This is a GAgent for sampling.");
    }
}