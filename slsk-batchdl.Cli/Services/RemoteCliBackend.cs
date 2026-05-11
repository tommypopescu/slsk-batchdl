using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sldl.Api;

namespace Sldl.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly SldlApiClient api;
    private readonly HubConnection connection;
    private readonly JsonSerializerOptions jsonOptions;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    internal static JsonSerializerOptions CreateJsonOptions()
        => SldlApiJson.CreateSerializerOptions();

    public RemoteCliBackend(string serverUrl)
    {
        var baseUri = SldlApiClient.NormalizeServerUrl(serverUrl);
        http = new HttpClient { BaseAddress = baseUri };
        jsonOptions = CreateJsonOptions();
        api = new SldlApiClient(http, jsonOptions);

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "api/events"))
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
                SldlApiJson.ConfigureSerializerOptions(options.PayloadSerializerOptions);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<ServerEventEnvelopeDto>("serverEvent", envelope =>
        {
            EventReceived?.Invoke(RehydrateEnvelope(envelope));
        });
    }

    public Task StartAsync(CancellationToken ct = default)
        => connection.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitExtractJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitSearchJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitTrackSearchJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitAlbumSearchJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitSongJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitAlbumJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitAggregateJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitAlbumAggregateJobAsync(request, ct), ct);

    public async Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(api.SubmitJobListAsync(request, ct), ct);

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeWorkflow", workflowId, ct);

    public Task SubscribeAllAsync(CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeAll", ct);

    private async Task<JobSummaryDto> SubmitAndSubscribeAsync(Task<JobSummaryDto> submit, CancellationToken ct)
    {
        var summary = await submit;
        await SubscribeWorkflowAsync(summary.WorkflowId, ct);
        return summary;
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
        => api.GetJobsAsync(query, ct);

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
        => api.GetJobDetailAsync(jobId, ct);

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.GetJobDetailByDisplayIdAsync(displayId, workflowId, ct);

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.GetWorkflowAsync(workflowId, ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, request, ct);

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => api.StartRetrieveFolderAsync(searchJobId, request, ct);

    public Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => api.RetrieveFolderAndWaitAsync(searchJobId, request, ct);

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
        => api.StartFileDownloadsAsync(searchJobId, request, ct);

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
        => api.StartFolderDownloadAsync(searchJobId, request, ct);

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.CompleteManualSelectionAsync(jobId, ct);

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
        => api.CancelJobAsync(jobId, ct);

    public Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.CancelJobByDisplayIdAsync(displayId, workflowId, ct);

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.CancelWorkflowAsync(workflowId, ct);

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
        => api.TryNextCandidateAsync(jobId, ct);

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.TryNextCandidateByDisplayIdAsync(displayId, workflowId, ct);

    private ServerEventEnvelopeDto RehydrateEnvelope(ServerEventEnvelopeDto envelope)
        => ServerEventPayloadConverter.RehydrateEnvelope(envelope, jsonOptions);
}
