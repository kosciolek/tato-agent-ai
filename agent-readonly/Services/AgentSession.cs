using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class AgentSession
    {
        private const int MaxToolRounds = 16;

        private readonly string model;
        private readonly string instructions;
        private readonly OpenAiClient client;
        private readonly ReadOnlyTools tools;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private string previousResponseId;

        public AgentSession(string model, string projectRoot, string apiKey, string contextText)
        {
            this.model = model;
            client = new OpenAiClient(apiKey);
            tools = new ReadOnlyTools(projectRoot);
            instructions = BuildInstructions(projectRoot, contextText);
        }

        public async Task<string> SendAsync(string userInput, Action<string> status, CancellationToken cancellationToken)
        {
            object input = userInput;
            StringBuilder final = new StringBuilder();

            for (int round = 0; round < MaxToolRounds; round++)
            {
                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    { "model", model },
                    { "input", input },
                    { "tools", ToolDefinitions() },
                    { "tool_choice", "auto" }
                };

                if (string.IsNullOrEmpty(previousResponseId))
                    request["instructions"] = instructions;
                else
                    request["previous_response_id"] = previousResponseId;

                if (status != null)
                    status("Thinking...");

                Dictionary<string, object> response = await client.CreateResponseAsync(request, cancellationToken).ConfigureAwait(false);
                previousResponseId = GetString(response, "id");

                List<Dictionary<string, object>> toolOutputs = new List<Dictionary<string, object>>();
                object[] outputItems = GetArray(response, "output");
                foreach (object rawItem in outputItems)
                {
                    Dictionary<string, object> item = rawItem as Dictionary<string, object>;
                    if (item == null)
                        continue;

                    string type = GetString(item, "type");
                    if (type == "message")
                    {
                        AppendMessageText(final, item);
                    }
                    else if (type == "function_call")
                    {
                        string name = GetString(item, "name");
                        string callId = GetString(item, "call_id");
                        string arguments = ArgumentsToJson(item.ContainsKey("arguments") ? item["arguments"] : null);
                        string toolOutput;

                        try
                        {
                            if (status != null)
                                status("Running " + name + "...");
                            toolOutput = tools.Run(name, arguments);
                        }
                        catch (Exception ex)
                        {
                            toolOutput = "ERROR: " + ex.Message;
                        }

                        toolOutputs.Add(new Dictionary<string, object>
                        {
                            { "type", "function_call_output" },
                            { "call_id", callId },
                            { "output", toolOutput }
                        });
                    }
                }

                if (toolOutputs.Count == 0)
                {
                    if (status != null)
                        status("Ready");
                    return final.ToString();
                }

                input = toolOutputs;
            }

            throw new InvalidOperationException("Stopped after " + MaxToolRounds + " tool rounds.");
        }

        private List<Dictionary<string, object>> ToolDefinitions()
        {
            List<Dictionary<string, object>> definitions = tools.Definitions();
            definitions.Add(new Dictionary<string, object> { { "type", "web_search_preview" } });
            return definitions;
        }

        private void AppendMessageText(StringBuilder final, Dictionary<string, object> item)
        {
            object[] content = GetArray(item, "content");
            foreach (object rawPart in content)
            {
                Dictionary<string, object> part = rawPart as Dictionary<string, object>;
                if (part == null)
                    continue;

                if (GetString(part, "type") != "output_text")
                    continue;

                string text = GetString(part, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (final.Length > 0)
                        final.AppendLine();
                    final.Append(text);
                }

                string citations = FormatCitations(GetArray(part, "annotations"));
                if (!string.IsNullOrEmpty(citations))
                    final.Append(citations);
            }
        }

        private string FormatCitations(object[] annotations)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> lines = new List<string>();

            foreach (object rawAnnotation in annotations)
            {
                Dictionary<string, object> annotation = rawAnnotation as Dictionary<string, object>;
                if (annotation == null || GetString(annotation, "type") != "url_citation")
                    continue;

                string url = GetString(annotation, "url");
                if (string.IsNullOrEmpty(url) || seen.ContainsKey(url))
                    continue;
                seen[url] = true;

                string title = GetString(annotation, "title");
                lines.Add(string.IsNullOrEmpty(title) ? "- " + url : "- " + title + ": " + url);
            }

            return lines.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + "Sources:" + Environment.NewLine + string.Join(Environment.NewLine, lines.ToArray());
        }

        private string ArgumentsToJson(object arguments)
        {
            if (arguments == null)
                return "{}";

            string text = arguments as string;
            if (text != null)
                return text;

            return serializer.Serialize(arguments);
        }

        private object[] GetArray(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value) || value == null)
                return new object[0];

            object[] array = value as object[];
            return array ?? new[] { value };
        }

        private string GetString(Dictionary<string, object> map, string name)
        {
            object value;
            return map.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
        }

        private string BuildInstructions(string projectRoot, string contextText)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("You are agent-readonly, a Windows desktop assistant for answering questions about a local codebase.");
            builder.AppendLine("The selected folder is the main working folder for this chat: " + projectRoot);
            builder.AppendLine("Use local file tools when questions are about files in the main working folder.");
            builder.AppendLine("You must not write files, run commands, edit code, use MCP, or claim that you changed anything locally.");
            builder.AppendLine("Use hosted web search only when the user needs current internet information or external facts.");
            builder.AppendLine("Keep answers concise and factual. Inspect local files before making claims about the codebase.");
            builder.AppendLine("When quoting code, use fenced blocks in this format:");
            builder.AppendLine("```codequote");
            builder.AppendLine("FILE relative/path.ext LINE: 42");
            builder.AppendLine("39 context line");
            builder.AppendLine("40 context line");
            builder.AppendLine("> 42 important line");
            builder.AppendLine("43 context line");
            builder.AppendLine("```");
            builder.AppendLine("For proposed changes, prefix removed lines with REMOVE and added lines with ADD inside the same fenced block.");

            if (!string.IsNullOrWhiteSpace(contextText))
            {
                builder.AppendLine();
                builder.AppendLine("User-provided CONTEXT.md:");
                builder.AppendLine(contextText);
            }

            return builder.ToString();
        }
    }
}
