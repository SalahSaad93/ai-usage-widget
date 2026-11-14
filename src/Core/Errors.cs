using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AiUsageWidget.Core
{
    // One place where exceptions become the short user-facing text shown on an Error row:
    // name-resolution and connect failures read as "can't reach server", TLS failures as
    // "secure connection failed", everything else as the innermost message cut to 60
    // characters ("error: An error occurred while sending the request" is no use to anyone).
    public static class Errors
    {
        public static string Describe(Exception ex)
        {
            if (ex == null) return "network error";

            bool tls = false;
            bool nameOrConnect = false;
            Exception current = ex;
            while (current != null)
            {
                var web = current as WebException;
                if (web != null)
                {
                    if (web.Status == WebExceptionStatus.SecureChannelFailure ||
                        web.Status == WebExceptionStatus.TrustFailure)
                        tls = true;
                    else if (web.Status == WebExceptionStatus.NameResolutionFailure ||
                        web.Status == WebExceptionStatus.ProxyNameResolutionFailure ||
                        web.Status == WebExceptionStatus.ConnectFailure)
                        nameOrConnect = true;
                }
                var socket = current as SocketException;
                if (socket != null && IsNameOrConnect(socket.SocketErrorCode))
                    nameOrConnect = true;
                if (current is AuthenticationException) tls = true;
                current = current.InnerException;
            }

            if (tls) return "secure connection failed";
            if (nameOrConnect) return "can't reach server";

            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            string message = inner.Message;
            if (string.IsNullOrEmpty(message)) return "network error";
            if (message.Length > 60) message = message.Substring(0, 60);
            return message;
        }

        // Non-200 HTTP statuses: "rate limited" for 429, "server error <code>" for 5xx,
        // "HTTP <code>" for the rest.
        public static string StatusText(int status)
        {
            if (status == 429) return "rate limited";
            if (status >= 500)
                return "server error " + status.ToString(CultureInfo.InvariantCulture);
            return "HTTP " + status.ToString(CultureInfo.InvariantCulture);
        }

        // Name resolution and connect failures; a socket timeout while connecting reads the
        // same way to a user. Other socket errors (reset, abort, …) keep their own message.
        static bool IsNameOrConnect(SocketError error)
        {
            switch (error)
            {
                case SocketError.HostNotFound:
                case SocketError.TryAgain:
                case SocketError.NoRecovery:
                case SocketError.NoData:
                case SocketError.ConnectionRefused:
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                case SocketError.NetworkDown:
                case SocketError.TimedOut:
                    return true;
                default:
                    return false;
            }
        }
    }
}
