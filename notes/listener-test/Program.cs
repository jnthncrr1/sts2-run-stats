var server = new RunStats.StatsHttpServer(9911, System.Text.Encoding.UTF8.GetBytes("<html>page</html>"), () => System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"), Console.WriteLine);
server.Start(); Console.WriteLine("listening"); Thread.Sleep(Timeout.Infinite);
