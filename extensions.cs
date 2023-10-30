public static class Extensions {
	public static IEnumerator<int> GetEnumerator(this Range range) => Enumerable.Range(range.Start.Value, range.End.Value - range.Start.Value).GetEnumerator();
}
