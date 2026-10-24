using System;
using System.Collections.Generic;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Tiny assertion helper so tests have no extra dependencies beyond GoDotTest.
/// GoDotTest is a runner only; it does not ship an assertion API.
/// </summary>
public static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception($"Assertion failed: {message}");
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Assertion failed: {message} (expected {expected}, got {actual})");
    }

    public static void Near(float expected, float actual, float tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"Assertion failed: {message} (expected {expected} ± {tolerance}, got {actual})");
    }

    public static void False(bool condition, string message) => True(!condition, message);

    /// <summary>Asserts that <paramref name="action"/> throws an exception of type <typeparamref name="T"/>.</summary>
    public static void Throws<T>(Action action, string message = "") where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Assertion failed: expected {typeof(T).Name} {message}");
    }
}

