if (args is ["sleep"])
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    return;
}

if (args is ["crash"])
{
    Environment.Exit(17);
    return;
}

if (args is ["output"])
{
    Console.Write(new string('x', 300_000));
    return;
}

Environment.Exit(2);
