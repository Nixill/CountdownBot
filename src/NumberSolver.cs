using Nixill.CalcLib.Modules;
using Nixill.CalcLib.Objects;
using Nixill.Utils;

namespace Nixill.Discord.Countdown;

public abstract class Expression : IComparable<Expression>
{
  List<int> UsedNumbers;
  List<int> UnusedNumbers;
  public IEnumerable<int> BaseConstituents => UsedNumbers.AsReadOnly();
  public IEnumerable<int> RemainingNumbers => UnusedNumbers.AsReadOnly();

  public abstract int Value { get; }

  public int CompareTo(Expression other)
  {
    if (other == null) throw new NullReferenceException();
    return Value.CompareTo(other.Value);
  }

  public abstract IEnumerable<int> GetConstituents();
  public abstract override string ToString();

  public bool IsCompatibleWith(Expression input)
    => UsedNumbers.ExceptInstances(input.UnusedNumbers).Count() == 0
    && input.UsedNumbers.ExceptInstances(UnusedNumbers).Count() == 0;

  public Expression(IEnumerable<int> usedNumbers, IEnumerable<int> unusedNumbers)
  {
    UsedNumbers = new(usedNumbers);
    UnusedNumbers = new(unusedNumbers);
  }
}

public class AdditiveExpression : Expression
{
  List<Expression> AddedExpressions;
  List<Expression> SubtractedExpressions;

  public IEnumerable<Expression> Addends => AddedExpressions.AsReadOnly();
  public IEnumerable<Expression> Subtrahends => SubtractedExpressions.AsReadOnly();

  public override int Value => AddedExpressions.Select(x => x.Value).Sum() - SubtractedExpressions.Select(x => x.Value).Sum();

  public AdditiveExpression(IEnumerable<int> usedNumbers, IEnumerable<int> unusedNumbers, Expression left, Expression right, bool subtract = false) : base(usedNumbers, unusedNumbers)
  {
    if (left is AdditiveExpression aLeft)
    {
      AddedExpressions = new(aLeft.AddedExpressions);
      SubtractedExpressions = new(aLeft.SubtractedExpressions);
    }
    else
    {
      AddedExpressions = new() { left };
      SubtractedExpressions = new();
    }

    List<Expression> subPlus = !subtract ? AddedExpressions : SubtractedExpressions;
    List<Expression> subMinus = !subtract ? SubtractedExpressions : AddedExpressions;

    if (right is AdditiveExpression aRight)
    {
      subPlus.AddRange(aRight.AddedExpressions);
      subMinus.AddRange(aRight.SubtractedExpressions);
    }
    else
    {
      subPlus.Add(right);
    }

    AddedExpressions = AddedExpressions.OrderByDescending(x => x.Value).ThenBy(x => x.ToString()).ToList();
    SubtractedExpressions = SubtractedExpressions.OrderByDescending(x => x.Value).ThenBy(x => x.ToString()).ToList();
  }

  public override string ToString()
  {
    if (AddedExpressions.Any())
      if (SubtractedExpressions.Any())
        return string.Join(" + ", AddedExpressions) + " - " + string.Join(" - ", SubtractedExpressions);
      else
        return string.Join(" + ", AddedExpressions);
    // A well formed AdditiveExpression should never reach the code below.
    else if (SubtractedExpressions.Any())
      return "- " + string.Join(" - ", SubtractedExpressions);
    else
      return "0";
  }

  IEnumerable<int> GetConstituentsExceptSelf()
    => AddedExpressions.SelectMany(x => x.GetConstituents()).Concat(SubtractedExpressions.SelectMany(x => x.GetConstituents())).Distinct();
  public override IEnumerable<int> GetConstituents()
    => GetConstituentsExceptSelf().Append(Value).Distinct();
}

public class MultiplicativeExpression : Expression
{
  List<Expression> MultipliedExpressions;
  List<Expression> DividedExpressions;

  public IEnumerable<Expression> Factors => MultipliedExpressions.AsReadOnly();
  public IEnumerable<Expression> Dividends => DividedExpressions.AsReadOnly();

  public override int Value
    => MultipliedExpressions.Select(x => x.Value).Aggregate(1, (l, r) => l * r)
      / DividedExpressions.Select(x => x.Value).Aggregate(1, (l, r) => l * r);

  public MultiplicativeExpression(IEnumerable<int> usedNumbers, IEnumerable<int> unusedNumbers, Expression left, Expression right, bool divide = false) : base(usedNumbers, unusedNumbers)
  {
    if (left is MultiplicativeExpression aLeft)
    {
      MultipliedExpressions = new(aLeft.MultipliedExpressions);
      DividedExpressions = new(aLeft.DividedExpressions);
    }
    else
    {
      MultipliedExpressions = new() { left };
      DividedExpressions = new();
    }

    List<Expression> subPlus = !divide ? MultipliedExpressions : DividedExpressions;
    List<Expression> subMinus = !divide ? DividedExpressions : MultipliedExpressions;

    if (right is MultiplicativeExpression aRight)
    {
      subPlus.AddRange(aRight.MultipliedExpressions);
      subMinus.AddRange(aRight.DividedExpressions);
    }
    else
    {
      subPlus.Add(right);
    }

    MultipliedExpressions.Sort();
    DividedExpressions.Sort();
  }

  public override string ToString()
  {
    var mult = MultipliedExpressions.Select(x => (x is AdditiveExpression) ? $"({x})" : $"{x}");
    var div = DividedExpressions.Select(x => (x is AdditiveExpression) ? $"({x})" : $"{x}");

    if (mult.Any())
      if (div.Any())
        return string.Join(" × ", mult) + " ÷ " + string.Join(" ÷ ", div);
      else
        return string.Join(" × ", mult);
    // A well formed MultiplicativeExpression should never reach the code below.
    else if (div.Any())
      return "1 ÷ " + string.Join(" ÷ ", div);
    else
      return "1";
  }

  IEnumerable<int> GetConstituentsExceptSelf()
    => MultipliedExpressions.SelectMany(x => x.GetConstituents()).Concat(DividedExpressions.SelectMany(x => x.GetConstituents())).Distinct();
  public override IEnumerable<int> GetConstituents()
    => GetConstituentsExceptSelf().Append(Value).Distinct();
}

public class SingleValueExpression : Expression
{
  int StoredValue;
  public override int Value => StoredValue;
  public override IEnumerable<int> GetConstituents() => EnumerableUtils.Of(StoredValue);
  public override string ToString() => StoredValue.ToString();

  public SingleValueExpression(int value, IEnumerable<int> unusedNumbers) : base(EnumerableUtils.Of(value), unusedNumbers) => StoredValue = value;
}

public static class NumberSolver
{
  public static IEnumerable<Expression> ExpressionsBetween(Expression left, Expression right)
  {
    Expression smaller, larger;

    if (left.Value < right.Value) { smaller = left; larger = right; }
    else { smaller = right; larger = left; }

    IEnumerable<int> constituents = smaller.GetConstituents().Concat(larger.GetConstituents()).Distinct().ToList();

    Expression added = new AdditiveExpression(larger.BaseConstituents.Concat(smaller.BaseConstituents),
      larger.RemainingNumbers.ExceptInstances(smaller.BaseConstituents), larger, smaller);
    if (!constituents.Contains(added.Value))
    {
      yield return added;
    }

    if (larger.Value - smaller.Value > 0)
    {
      Expression subtracted = new AdditiveExpression(larger.BaseConstituents.Concat(smaller.BaseConstituents),
        larger.RemainingNumbers.ExceptInstances(smaller.BaseConstituents), larger, smaller, true);
      if (!constituents.Contains(subtracted.Value))
      {
        yield return subtracted;
      }
    }

    Expression multiplied = new MultiplicativeExpression(larger.BaseConstituents.Concat(smaller.BaseConstituents),
      larger.RemainingNumbers.ExceptInstances(smaller.BaseConstituents), larger, smaller);
    if (!constituents.Contains(multiplied.Value))
    {
      yield return multiplied;
    }

    if (larger.Value % smaller.Value == 0)
    {
      Expression divided = new MultiplicativeExpression(larger.BaseConstituents.Concat(smaller.BaseConstituents),
        larger.RemainingNumbers.ExceptInstances(smaller.BaseConstituents), larger, smaller, true);
      if (!constituents.Contains(divided.Value))
      {
        yield return divided;
      }
    }
  }

  public static IEnumerable<Expression> GetExpressionsFor(IEnumerable<int> numbers)
  {
    Dictionary<int /*number of given numbers*/, Dictionary<string, Expression> /*expressions with that many given numbers*/> expressions = new();
    // This is 1-indexed because it's a dictionary and not a list!

    expressions[1] = new();

    // First tier
    foreach (int num in numbers.Distinct())
    {
      Expression expr = new SingleValueExpression(num, numbers.ExceptInstances(EnumerableUtils.Of(num)));
      expressions[1][expr.ToString()] = expr;
      yield return expr;
    }

    // Later tiers
    foreach (int level in Enumerable.Range(2, numbers.Count() - 1))
    {
      var newExpressions = expressions[level] = new();

      int l = 1;
      int r = level - 1;

      while (r > l)
      {
        foreach (Expression left in expressions[l].Values)
        {
          foreach (Expression right in expressions[r].Values.Where(e => e.IsCompatibleWith(left)))
          {
            foreach (Expression nu in ExpressionsBetween(left, right))
            {
              newExpressions[nu.ToString()] = nu;
              yield return nu;
            }
          }
        }

        r--;
        l++;
      }

      if (r == l)
      {
        foreach (IEnumerable<string> pair in expressions[l].Keys.PermutationsDistinct(2))
        {
          Expression left = expressions[l][pair.First()];
          Expression right = expressions[l][pair.Last()];

          if (!left.IsCompatibleWith(right)) continue;

          foreach (Expression nu in ExpressionsBetween(left, right))
          {
            newExpressions[nu.ToString()] = nu;
            yield return nu;
          }
        }

        foreach (Expression ex in expressions[l].Values.Where(e => e.IsCompatibleWith(e)))
        {
          foreach (Expression nu in ExpressionsBetween(ex, ex))
          {
            newExpressions[nu.ToString()] = nu;
            yield return nu;
          }
        }
      }
    }
  }
}

public static class ExpressionConverter
{
  public static CalcObject ToCalcLib(Expression expr)
  {
    if (expr is SingleValueExpression svExpr)
    {
      return new CalcNumber(expr.Value);
    }
    else if (expr is AdditiveExpression adExpr)
    {
      List<CalcObject> added = adExpr.Addends
        .Select(ToCalcLib)
        .ToList();
      List<CalcObject> subtracted = adExpr.Subtrahends
        .Select(ToCalcLib)
        .ToList();

      CalcObject obj = added.Pop();

      foreach (CalcObject add in added)
      {
        obj = new CalcOperation(obj, MainModule.BinaryPlus, add);
      }

      foreach (CalcObject sub in subtracted)
      {
        obj = new CalcOperation(obj, MainModule.BinaryMinus, sub);
      }
    }
    else if (expr is MultiplicativeExpression muExpr)
    {
      List<CalcObject> multiplied = muExpr.Factors
        .Select(ToCalcLib)
        .ToList();
      List<CalcObject> divided = muExpr.Dividends
        .Select(ToCalcLib)
        .ToList();

      CalcObject obj = multiplied.Pop();

      foreach (CalcObject mul in multiplied)
      {
        obj = new CalcOperation(obj, MainModule.BinaryPlus, mul);
      }

      foreach (CalcObject div in divided)
      {
        obj = new CalcOperation(obj, MainModule.BinaryMinus, div);
      }
    }

    throw new InvalidOperationException();
  }

  public static IEnumerable<int> GetBaseNumbers(CalcObject obj)
  {
    if (obj is CalcOperation oper)
    {
      foreach (int bn in GetBaseNumbers(oper.Left)) yield return bn;
      foreach (int bn in GetBaseNumbers(oper.Right)) yield return bn;
    }
    else if (obj is CalcNumber num)
    {
      yield return (int)num.Value;
    }
  }
}