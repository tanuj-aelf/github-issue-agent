# Aevatar Starter Template

This repository contains three projects that work together to demonstrate the use of the Aevatar framework with Orleans. The projects are:

1. **Silo Project**(AevatarTemplate.Silo)
2. **Client Project**(AevatarTemplate.Client)
3. **GAgents Project**(AevatarTemplate.GAgents)

## Projects Overview

### Silo Project

The Silo project is responsible for starting the Orleans Silo Host. It hosts all the GAgents as grains.

### Client Project

The Client project is used to create and interact with GAgents. It connects to the Silo Host and performs operations on the GAgents.

### GAgents Project

The GAgents project is used to define new GAgent types. You can refer to the `SampleGAgent` for an example of how to define a new GAgent type.

## Startup Process

To start the projects, follow these steps:

1. **Start the Silo Project**: This will start the Orleans Silo Host and host all the GAgents as grains.
2. **Start the Client Project**: This will connect to the Silo Host and allow you to create and interact with GAgents.

## Running the Projects

### Start the Silo Project

Navigate to the Silo project directory and run the following command:

```sh
dotnet run --project src/AevatarTemplate.Silo
```

### Start the Client Project

Navigate to the Client project directory and run the following command:

```sh
dotnet run --project src/AevatarTemplate.Client
```

By following these steps, you will be able to start the Silo Host and interact with GAgents using the Client project.

## Technologies Used
- [.NET Core](https://dotnet.microsoft.com/)
- [Orleans](https://dotnet.github.io/orleans/)
- [AevatarAI](https://aevatar.ai/)
