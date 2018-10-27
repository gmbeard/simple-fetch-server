Simple Fetch Server
-------------------

This is a simple, multi-threaded server application that accepts 
incoming requests to fetch the content from a remote web address.

The server listens for TCP connections on `127.0.0.1`, port `7654`.

The format of a server request is simply, `GET [URL]\r\n`. For example, 
`GET https://google.com\r\n`.

An incoming connection is closed after one request.
