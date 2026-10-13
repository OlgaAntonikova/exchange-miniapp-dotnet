using Exchange.Api.Rates;
using Exchange.Domain.Directions;

namespace Exchange.Api.Tests.Rates;

/// <summary>A provider whose next answers the test decides: a snapshot, a failure, or a wait.</summary>
internal sealed class FakeRateProvider(string name, TimeProvider time) : IRateProvider
{
    private readonly Queue<Func<Task<RateSnapshot>>> _answers = new();

    public string Name => name;

    public int Calls { get; private set; }

    /// <summary>The next fetch succeeds with these mids, taken now, valid until <paramref name="validFor"/> from now.</summary>
    public FakeRateProvider Succeeds(Dictionary<Currency, decimal> mids, TimeSpan? validFor = null)
    {
        _answers.Enqueue(() =>
        {
            var now = time.GetUtcNow();
            return Task.FromResult(new RateSnapshot(name, now, mids, now + (validFor ?? TimeSpan.FromDays(30))));
        });
        return this;
    }

    public FakeRateProvider Fails(string reason)
    {
        _answers.Enqueue(() => Task.FromException<RateSnapshot>(new RateProviderException(reason)));
        return this;
    }

    public FakeRateProvider Waits(Task<RateSnapshot> answer)
    {
        _answers.Enqueue(() => answer);
        return this;
    }

    public Task<RateSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return _answers.Count > 0 ? _answers.Dequeue()() : Task.FromException<RateSnapshot>(new RateProviderException("no_answer_scripted"));
    }
}
