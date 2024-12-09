using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ����������� ����������� ������ (wwwroot)
app.UseStaticFiles();

// ��������� �������� � API
app.MapPut("/experiments", (ExperimentParameters parameters) =>
{
    lock (ExperimentStore._lock)
    {
        var id = Guid.NewGuid();
        ExperimentStore.Experiments[id] = new Experiment(parameters);
        return Results.Ok(
            new
            {
                ExperimentId = id,
                NodesAmount = ExperimentStore.Experiments[id].NodesAmount,
                Matrix = JSONConverter.MatrixToJson(ExperimentStore.Experiments[id].Matrix),
                Best = ExperimentStore.Experiments[id].Best
            }
            );
    }
});

app.MapPost("/experiments/{id:guid}", (Guid id) =>
{
    lock (ExperimentStore._lock)
    {
        if (!ExperimentStore.Experiments.TryGetValue(id, out var experiment))
            return Results.NotFound("Experiment not found");

        var result = experiment.RunStep();
        return Results.Ok(result);
    }
});

app.MapDelete("/experiments/{id:guid}", (Guid id) =>
{
    lock (ExperimentStore._lock)
    {
        if (ExperimentStore.Experiments.Remove(id))
            return Results.Ok("Experiment deleted");

        return Results.NotFound("Experiment not found");
    }
});




app.MapPost("/experiments/{id:guid}/start", async (Guid id) =>
{
    lock (ExperimentStore._lock)
    {
        if (!ExperimentStore.Experiments.TryGetValue(id, out var experiment))
            return Results.NotFound("Experiment not found");

        // ���� �������� ��� ��������, ���������� ������
        if (ExperimentStore.EvolutionTokens.ContainsKey(id))
            return Results.BadRequest("Evolution already running for this experiment.");

        var cts = new CancellationTokenSource();
        ExperimentStore.EvolutionTokens[id] = cts;

        // ������ �������� � ��������� ������
        _ = Task.Run(async () =>
        {
            try
            {
                for (long i = 0; (experiment.Config.Epochs == -1) || (i < experiment.Config.Epochs); i++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    var result = experiment.RunStep();
                }
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine($"Experiment {id} evolution stopped.");
            }
            finally
            {
                ExperimentStore.EvolutionTokens.Remove(id);
            }

        });

        return Results.Ok("Evolution started.");
    }
});

app.MapPost("/experiments/{id:guid}/stop", (Guid id) =>
{
    lock (ExperimentStore._lock)
    {
        if (!ExperimentStore.EvolutionTokens.TryGetValue(id, out var cts))
            return Results.BadRequest("No running evolution for this experiment.");

        cts.Cancel(); // ��������� ��������
        return Results.Ok("Evolution stopped.");
    }
});

app.MapGet("/experiments/{id:guid}/stream", async (HttpContext context, Guid id) =>
{
        if (!ExperimentStore.Experiments.TryGetValue(id, out var experiment))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Experiment not found");
            return;
        }

        context.Response.ContentType = "text/event-stream";

        while (true)
        {
            if (!ExperimentStore.EvolutionTokens.ContainsKey(id)) break;

            var fitness = experiment.FScore; // �������� ������� �������� fitness
            var data = $"data: {{\"fScore\": {fitness}," +
            $" \"epochs\": {experiment.Epochs}," +
            $" \"best\": \"{experiment.Best}\"}}\n\n";

            await context.Response.WriteAsync(data);
            await context.Response.Body.FlushAsync();
            await Task.Delay(500); // ���������� ������ ������ 1 ���
        }
});





// ������� �� ��������� ��� ����������� HTML-��������
app.MapFallbackToFile("index.html");

app.Run();

// ������ ��� ���������� ������������
public record ExperimentParameters(
    int NodesAmount,
    int Epochs,
    int PopulationSize,
    double MutationProbability,
    double CrossoverProbability,
    double SurvivorsPart
);

