Realtime Code Template to create new GAgent:

```csharp
[GenerateSerializer]
public class $GAGENTTYPE$GAgentState : StateBase
{

}

[GenerateSerializer]
public class $GAGENTTYPE$StateLogEvent : StateLogEventBase<$GAGENTTYPE$StateLogEvent>
{

}

[GAgent]
public class $GAGENTTYPE$GAgent : GAgentBase<$GAGENTTYPE$GAgentState, $GAGENTTYPE$StateLogEvent>
{
    public $GAGENTTYPE$GAgent(ILogger<$GAGENTTYPE$GAgent> logger) : base(logger)
    {
    }

    public override Task<string> GetDescriptionAsync()
    {
        return Task.FromResult("This is a GAgent for");
    }
}
```

With Configuration:
```csharp
[GenerateSerializer]
public class $GAGENTTYPE$GAgentState : StateBase
{

}

[GenerateSerializer]
public class $GAGENTTYPE$StateLogEvent : StateLogEventBase<$GAGENTTYPE$StateLogEvent>
{

}

[GenerateSerializer]
public class $GAGENTTYPE$ConfigurationEvent : ConfigurationEventBase
{

}


[GAgent]
public class $GAGENTTYPE$GAgent : GAgentBase<$GAGENTTYPE$GAgentState, $GAGENTTYPE$StateLogEvent, EventBase, $GAGENTTYPE$ConfigurationEvent>
{
    public $GAGENTTYPE$GAgent(ILogger<$GAGENTTYPE$GAgent> logger) : base(logger)
    {
    }

    public override Task<string> GetDescriptionAsync()
    {
        return Task.FromResult("This is a GAgent for");
    }
    
    public override async Task PerformConfigAsync($GAGENTTYPE$ConfigurationEvent configurationEvent)
    {

    }
}
```