namespace Nixill.Utils;

public static class MoreUtils
{
  public static IEnumerable<T> ExceptInstances<T>(this IEnumerable<T> first, IEnumerable<T> second)
  {
    Dictionary<T, int> counts = second.GroupBy(t => t).Select(g => new KeyValuePair<T, int>(g.Key, g.Count())).ToDictionary();

    foreach (T item in first)
    {
      if (counts.ContainsKey(item) && counts[item] > 0)
      {
        counts[item]--;
      }
      else
      {
        yield return item;
      }
    }
  }

  public static IEnumerable<T> IntersectInstances<T>(this IEnumerable<T> first, IEnumerable<T> second)
  {
    Dictionary<T, int> counts = second.GroupBy(t => t).Select(g => new KeyValuePair<T, int>(g.Key, g.Count())).ToDictionary();

    foreach (T item in first)
    {
      if (counts.ContainsKey(item) && counts[item] > 0)
      {
        counts[item]--;
        yield return item;
      }
    }
  }
}

