using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MotoRouteFinder.Services;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class JobProgress
{
    public string JobId { get; init; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double OverallProgress { get; set; }
    public string? StepMessage { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public CancellationTokenSource Cts { get; init; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class JobProgressStore
{
    private readonly ConcurrentDictionary<string, JobProgress> _jobs = new();

    private const double EvictionAgeMinutes = 5;

    public string CreateJob()
    {
        SweepExpiredJobs();
        var job = new JobProgress { JobId = Guid.NewGuid().ToString("N") };
        _jobs[job.JobId] = job;
        return job.JobId;
    }

    private void SweepExpiredJobs()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                if ((now - kvp.Value.LastUpdated).TotalMinutes > EvictionAgeMinutes)
                {
                    if (_jobs.TryRemove(kvp.Key, out var expired))
                        expired.Cts.Dispose();
                }
            }
        }
    }

    public void UpdateProgress(string jobId, double fraction, string? message)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.OverallProgress = fraction;
            job.StepMessage = message;
            job.LastUpdated = DateTime.UtcNow;
            if (job.Status == JobStatus.Pending)
                job.Status = JobStatus.Running;
        }
    }

    public void Complete(string jobId, object result)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = JobStatus.Completed;
            job.OverallProgress = 1.0;
            job.Result = result;
            job.LastUpdated = DateTime.UtcNow;
        }
    }

    public void Fail(string jobId, string error)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = JobStatus.Failed;
            job.Error = error;
            job.LastUpdated = DateTime.UtcNow;
        }
    }

    public void Cancel(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Cts.Cancel();
            job.Status = JobStatus.Cancelled;
            job.LastUpdated = DateTime.UtcNow;
        }
    }

    public JobProgress? TryGet(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return null;

        // Lazy eviction: remove terminal jobs older than 5 minutes
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            if ((DateTime.UtcNow - job.LastUpdated).TotalMinutes > EvictionAgeMinutes)
            {
                _jobs.TryRemove(jobId, out _);
                job.Cts.Dispose();
                return null;
            }
        }

        return job;
    }
}
