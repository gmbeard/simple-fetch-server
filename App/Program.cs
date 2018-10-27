using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text;

namespace App
{
class Program
{
    const int NUMBER_OF_THREADS = 4;

    //  `running` is used as a flag to our threads' loops. `activeCount`
    //  is used to tell us how many threads are still running...
    static long running = 1;
    static long activeCount = 0;
    static HttpClient httpClient = new HttpClient();

    static void Main(string[] args)
    {
        const int serverPort = 7654;
        var listener = CreateListener(serverPort);
        var task = Task.Factory.StartNew(() => Run(listener));

        Console.Write($"Server listening on port {serverPort}. " +
                      "Press Enter to quit.");
        Console.ReadLine();

        Console.Write("Shutting down... ");
        Interlocked.Exchange(ref running, 0);
        listener.Close();

        try { task.Wait(); } catch { }
        Console.WriteLine("done!");
    }

    /// <summary>
    /// Creates a listening socket that is bound to a local IP address
    /// </summary>
    /// <param name="port">The port to listen on</param>
    /// <param name="localAddress">The IP address to listen on</param>
    /// <returns>
    /// A new <c>Socket</c> object
    /// </returns>
    static Socket CreateListener(int port, 
                                 String localAddress = "127.0.0.1") 
    {
        // Setup our listening socket...
        var ipAddress = IPAddress.Parse(localAddress);
        var listener = new Socket(ipAddress.AddressFamily,
                                  SocketType.Stream,
                                  ProtocolType.Tcp);

        listener.Bind(new IPEndPoint(ipAddress, port));
        listener.Listen(100);
        return listener;
    }

    /// <summary>
    /// Attempts to decode a UTF-8 string from <paramref name="buffer" />
    /// </summary>
    /// <param name="buffer">The bytes from which to read the value</param>
    /// <param name="length">The length of the input buffer</param>
    /// <param name="val">The ouput value, if successful</param>
    /// <returns><c>true</c> if successful, <c>false</c> otherwise</returns>
    static bool TryDecodeUtf8String(Byte[] buffer, 
                                    int length, 
                                    out String val) 
    {
        val = null;
        try {
            val = Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (ArgumentException) {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reads a string terminated by a newline from <paramref name="s" />
    /// </summary>
    /// <param name="s">The socket to read from</param>
    static String ReadRequest(Socket s)
    {
        var buffer = new Byte[1024];
        var received = 0;

        do {
            received += s.Receive(buffer, 
                                  received, 
                                  buffer.Length - received,
                                  SocketFlags.None);
            String input = null;
            if (!TryDecodeUtf8String(buffer, received, out input)) {
                continue;
            }

            var parts = input.Split(new String[] { Environment.NewLine },
                                    StringSplitOptions.None);

            if (parts.Length == 1) {
                continue;
            }

            return parts[0];
        }
        while (true);
    }

    /// <summary>
    /// Fetches the content from the remote web address. The format of a 
    /// request must be <c>GET [DOMAIN NAME]</c>. For example, 
    /// <c>GET www.google.com</c>
    /// </summary>
    /// <param name="request">The input</param>
    static String DoRequest(String request) {
        var parts = request.Split(new Char[] { ' ' },
                                  StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ) {
            throw new ArgumentException(
                "The format of the request is invalid: Not enough parameters",
                nameof(request)
            );
        }

        if (String.Compare("GET", parts[0], true) != 0) {
            throw new ArgumentException(
                "The format of the request is invalid: Recieved " +
                $"'{parts[0]}' instead of 'GET'",
                nameof(request)
            );
        }

        var response = httpClient.GetAsync($"{parts[1]}").Result;
        return response.Content.ReadAsStringAsync().Result;
    }

    /// <summary>
    /// Writes <paramref name="response" /> out to socket <paramref name="s" />
    /// </summary>
    /// <param name="response">The response to send</param>
    /// <param name="request">The socket to which to send the response</param>
    static void WriteResponse(String response, Socket s) {
        var data = Encoding.UTF8.GetBytes(response);
        var written = 0;
        do {
            written += s.Send(data, 
                              written, 
                              data.Length - written,
                              SocketFlags.None);
        }
        while (written < data.Length);
    }

    /// <summary>
    /// Represents a single thread of work.
    /// </summary>
    /// <param name="queue">
    /// A shared work queue.
    /// </param>
    /// <param name="workReady">
    /// A signal used to inform the thread that new work has been 
    /// added to <paramref name="queue" />.
    /// </param>
    static void RunThread(ConcurrentQueue<Socket> queue,
                          WaitHandle workReady) 
    {
        try {
            while (true) {
                Socket s = null;
                while (Interlocked.Read(ref running) == 1 && 
                       !queue.TryDequeue(out s))
                {
                    workReady.WaitOne();
                }

                if (Interlocked.Read(ref running) != 1) {
                    break;
                }

                try {
                    using (s) {
                        var request = ReadRequest(s);
                        var data = DoRequest(request);
                        WriteResponse(data, s);
                    }
                }
                catch (Exception e) {
                    Console.Error.WriteLine(
                        "Error processing request: " +
                        $"{e.Message}\r\n{e.StackTrace}");
                }
            }
        }
        finally {
            Interlocked.Decrement(ref activeCount);
        }
    }

    /// <summary>
    /// Runs the main incoming connection loop until either `running == 0`,
    /// or `listener` is closed
    /// </summary>
    /// <param name="listener">A socket that is already bound to a local
    /// address and listening for connections.
    /// </param>
    static void Run(Socket listener)
    {
        // This will contain the work to be done by our
        // threads...
        var workQueue = new ConcurrentQueue<Socket>();

        // This is used to signal our threads that there
        // is new work in the queue...
        var workReady = new AutoResetEvent(false);

        //  Create our thread pool...
        var threads = new Task[NUMBER_OF_THREADS];
        for (var i = 0; i < threads.Length; ++i) {
            // The main thread increases the `activeCount`. Each
            // thread is responsible for decreasing it, once they're
            // done...
            Interlocked.Increment(ref activeCount);

            // Create a thread. Our `RunThread` function does the 
            // actual work...
            threads[i] = Task.Factory.StartNew(
                () => RunThread(workQueue, workReady)
            );
        }

        try {
            // Socket accept loop...
            while (Interlocked.Read(ref running) == 1) {
                // Push any new incoming sockets onto the 
                // work queue...
                workQueue.Enqueue(listener.Accept());
                // ...And tell our threads...
                workReady.Set();
            }
        }
        finally {
            // Loop to ensure that all the threads have ended
            // before we exit...
            while (Interlocked.Read(ref activeCount) > 0) {
                // Notify a thread that we're shutting down...
                workReady.Set();

                // Wait for at least one thread to finish...
                Task.WaitAny(threads, TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
}
