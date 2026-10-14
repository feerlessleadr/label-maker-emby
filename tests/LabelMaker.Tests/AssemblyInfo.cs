using Xunit;

// TagLibSharp keeps its ID3 settings in static (process-wide) fields, and some tests call TagLib directly, so tests run one at a time.
// The whole suite takes about a second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
