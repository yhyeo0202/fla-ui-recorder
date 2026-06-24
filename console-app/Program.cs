using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

Window window;
FlaUI.Core.Application app;
using UIA3Automation automation = new();

List<ProcessFilter> listProcessFilter = new()
{
    new ProcessFilter
    {
        mainWindowTitle = "Microsoft Visual Studio"
    },
    new ProcessFilter
    {
        mainWindowTitle = "Microsoft Visual Studio",
        bContains = true
    },
};

int processFilterIndex = 0;

List<ControlType> listEventControlType = new()
{
    ControlType.Button,
    ControlType.Custom,
    ControlType.ComboBox,
    ControlType.Edit,
    ControlType.ListItem,
    ControlType.MenuItem,
    ControlType.TabItem
};
List<AutomationEventHandlerBase> listEventHandler = new();

List<ControlType> listStructureChangedControlType = new()
{
    ControlType.Menu,
    ControlType.MenuItem
};
Dictionary<ControlType, HashSet<string>> dictPreviousStructure = listStructureChangedControlType.ToDictionary(i => i, i => new HashSet<string>());
StructureChangedEventHandlerBase structureChangedHandler = null;

AutomationElement[] arrayPreviousElement;
List<StepConfig> listStep = new();
Lock eventLock = new();
bool bSuppressStructureChangedEvent = false;

void Attach2Process()
{
    Process process = null;

    while (process == null)
    {
        Thread.Sleep(200);
        Process[] arrayProcess = Process.GetProcessesByName("devenv");
        process = arrayProcess.Where(i => !i.MainWindowTitle.Contains("fla-ui-recorder"))
            .Where(j =>
            {
                if (listProcessFilter[processFilterIndex].bContains)
                {
                    return j.MainWindowTitle.Contains(listProcessFilter[processFilterIndex].mainWindowTitle);
                }

                return j.MainWindowTitle == listProcessFilter[processFilterIndex].mainWindowTitle;
            })
            .OrderByDescending(k => k.StartTime)
            .FirstOrDefault();
    }

    processFilterIndex = processFilterIndex < listProcessFilter.Count - 1 ? processFilterIndex + 1 : processFilterIndex;
    app = FlaUI.Core.Application.Attach(process);
    window = app.GetMainWindow(automation);
    Thread.Sleep(2000);

    return;
}

string GetXPath(AutomationElement element)
{
    string xPath = "";
    AutomationElement recursingElement = element;

    do
    {
        if (!string.IsNullOrEmpty(xPath))
        {
            xPath = $"/{xPath}";
        }

        string automationId = null;

        try
        {
            automationId = $"@AutomationId='{recursingElement.AutomationId}' and ";
        }
        catch
        {
            automationId = "";
        }

        string name = $"@Name='{recursingElement.Name}'";
        xPath = $"{recursingElement.ControlType}[{automationId}{name}]{xPath}";
        recursingElement = recursingElement.Parent;
    } while (recursingElement.ControlType != ControlType.Window);

    AutomationElement[] arrayElement = window.FindAllByXPath(xPath);

    if (arrayElement.Length < 2)
    {
        return xPath;
    }

    HashSet<string> setSelectedIdNameType = new();

    foreach (AutomationElement descendentElement in element.FindAllDescendants())
    {
        setSelectedIdNameType.Add($"{descendentElement.AutomationId},{descendentElement.Name},{descendentElement.ControlType}");
    }

    HashSet<string> setIdNameType = new();
    HashSet<string> setUniqueIdNameType = null;

    foreach (AutomationElement similarElement in arrayElement)
    {
        setIdNameType.Clear();

        foreach (AutomationElement descendentElement in similarElement.FindAllDescendants())
        {
            setIdNameType.Add($"{descendentElement.AutomationId},{descendentElement.Name},{descendentElement.ControlType}");
        }

        HashSet<string> setExclusiveIdNameType = setSelectedIdNameType.Except(setIdNameType).ToHashSet();

        if (setExclusiveIdNameType.Count == 0)
        {
            continue;
        }
        else if (setUniqueIdNameType == null)
        {
            setUniqueIdNameType = setExclusiveIdNameType;
        }
        else
        {
            setUniqueIdNameType.IntersectWith(setExclusiveIdNameType);
        }
    }

    List<string> listIdNameType = setUniqueIdNameType.First()
        .Split(',')
        .ToList();
    xPath = $"{xPath[..^1]} and .//{listIdNameType[2]}[@AutomaticId='{listIdNameType[0]}' and @Name='{listIdNameType[1]}']]";

    return xPath;
}

void AddEventStep(AutomationElement element, EventId eventId)
{
    Console.WriteLine("AddEventStep");
    bSuppressStructureChangedEvent = true;
    string xPath = GetXPath(element);
    bool bRefreshWindow = false;

    if (eventId == automation.EventLibrary.Text.TextChangedEvent && element.IsEnabled)
    {
        if (listStep.Last().xPath == xPath)
        {
            listStep.Last().text = element.AsTextBox().Text;
        }
        else
        {
            listStep.Add(new StepConfig
            {
                controlType = element.ControlType.ToString(),
                xPath = xPath,
                text = element.AsTextBox().Text
            });
        }
    }
    else if(eventId == automation.EventLibrary.Invoke.InvokedEvent)
    {
        listStep.Add(new StepConfig
        {
            controlType = element.ControlType.ToString(),
            xPath = xPath
        });

        if(element.ControlType == ControlType.MenuItem)
        {
            bRefreshWindow = true;
        }
    }
    else if(element.ControlType == ControlType.ComboBox)
    {
        ComboBoxItem selectedItem = element.AsComboBox().SelectedItem;

        if(selectedItem != null)
        {
            listStep.Add(new StepConfig
            {
                controlType = element.ControlType.ToString(),
                xPath = xPath,
                text = selectedItem.Text
            });
        }
    }
    else
    {
        listStep.Add(new StepConfig
        {
            controlType = element.ControlType.ToString(),
            xPath = xPath
        });
    }

    RegisterTargetedAutomationEvent(bRefreshWindow);

    foreach (StepConfig step in listStep)
    {
        Console.WriteLine($"debug0 {step.controlType}; {step.xPath}; {step.text}; {step.bRightClick}");
    }

    return;
}

void AddStructureChangedStep(AutomationElement element, StructureChangeType changeType, int[] runtimeId)
{
    if(bSuppressStructureChangedEvent)
    {
        bSuppressStructureChangedEvent = false;

        return;
    }

    Console.WriteLine("AddStructureChangedStep");

    try
    {
        if(element.ControlType != ControlType.Window)
        {
            return;
        }
    }
    catch
    {
        return;
    }

    try
    {
        window = app.GetMainWindow(automation);
        ControlType changedControlType = default;
        bool bStructureAdded = false;
        bool bStructureRemoved = false;

        foreach (ControlType controlType in listStructureChangedControlType)
        {
            HashSet<string> setCurrentStructure = window.FindAllDescendants(cf => cf.ByControlType(controlType))
                .Select(i => $"{i.AutomationId},{i.Name}")
                .ToHashSet();

            if (setCurrentStructure.Except(dictPreviousStructure[controlType]).Count() > 0)
            {
                changedControlType = controlType;
                bStructureAdded = true;

                break;
            }
            else if(dictPreviousStructure[controlType].Except(setCurrentStructure).Count() > 0)
            {
                bStructureRemoved = true;

                break;
            }
        }

        if(bStructureAdded)
        {
            if(changedControlType == ControlType.Menu || changedControlType == ControlType.MenuItem)
            {
                Point mousePosition = Mouse.Position;
                int minArea = int.MaxValue;
                AutomationElement clickedElement = null;

                foreach(AutomationElement descendantElement in arrayPreviousElement)
                {
                    if (descendantElement.BoundingRectangle.Contains(mousePosition))
                    {
                        int area = descendantElement.BoundingRectangle.Width * descendantElement.BoundingRectangle.Height;

                        if (area < minArea)
                        {
                            minArea = area;
                            clickedElement = descendantElement;
                        }
                    }
                }

                if(clickedElement != null)
                {
                    string xPath = $".//{clickedElement.ControlType}[@AutomationId='{clickedElement.AutomationId}' and @Name='{clickedElement.Name}']";
                    listStep.Add(new StepConfig
                    {
                        controlType = clickedElement.ControlType.ToString(),
                        xPath = xPath,
                        bRightClick = changedControlType == ControlType.Menu
                    });
                }
            }

            RegisterTargetedAutomationEvent(); 
        }
        else if(bStructureRemoved)
        {
            RegisterTargetedAutomationEvent();
        }
    }
    catch { }

    return;
}

void RegisterTargetedAutomationEvent(bool bRefreshWindow = false)
{
    try
    {
        foreach (AutomationEventHandlerBase eventHandler in listEventHandler)
        {
            eventHandler.Dispose();
        }

        listEventHandler.Clear();
        structureChangedHandler?.Dispose();
    }
    catch
    {
        listEventHandler.Clear();
        structureChangedHandler = null;
        Attach2Process();
    }
    /*
    Console.WriteLine($"debug4 {window.FindAllDescendants().Count()}");
    if(window.FindAllDescendants().Count() == 0)
    {
        Attach2Process();
    }
    
    window = app.GetMainWindow(automation);
    */

    Thread.Sleep(1500);

    if(bRefreshWindow)
    {
        window = app.GetMainWindow(automation);
    }

    arrayPreviousElement = window.FindAllDescendants();

    foreach (ControlType controlType in listEventControlType)
    {
        foreach (AutomationElement element in arrayPreviousElement.Where(i => i.ControlType == controlType))
        {
            Console.WriteLine($"debug1 {element.Name} {element.ControlType}");
            List<EventId> listEventId = new();

            switch (controlType)
            {
                case ControlType.Button:
                case ControlType.MenuItem:
                    listEventId.Add(automation.EventLibrary.Invoke.InvokedEvent);
                    break;
                case ControlType.ComboBox:
                    listEventId.Add(automation.EventLibrary.Selection.InvalidatedEvent);
                    break;
                case ControlType.Edit:
                    listEventId.Add(automation.EventLibrary.Text.TextChangedEvent);
                    break;
                case ControlType.Custom:
                case ControlType.ListItem:
                case ControlType.TabItem:
                    listEventId.Add(automation.EventLibrary.SelectionItem.ElementSelectedEvent);
                    break;
                /*
                case ControlType.Menu:
                    Console.WriteLine($"debug2 {GetXPath(element)}");
                    break;
                */
            }
            
            foreach (EventId eventId in listEventId)
            {
                AutomationEventHandlerBase eventHandler = element.RegisterAutomationEvent(eventId, TreeScope.Element, AddEventStep);
                listEventHandler.Add(eventHandler);
            }
        }
    }

    foreach(ControlType controlType in dictPreviousStructure.Keys)
    {
        dictPreviousStructure[controlType].Clear();
    }
    
    if(window.Parent != null)
    {
        structureChangedHandler = window.Parent.RegisterStructureChangedEvent(TreeScope.Subtree, AddStructureChangedStep);
    }
    
    /*
    Thread.Sleep(3000);
    Console.WriteLine("\n\n");
    RegisterTargetedAutomationEvent();
    */
    return;
}

Process.Start("C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\Common7\\IDE\\devenv.exe");
Attach2Process();
RegisterTargetedAutomationEvent();
Console.ReadKey();

File.WriteAllText(
    "C:\\fla-ui-recorder\\xPath.json",
    JsonSerializer.Serialize(
        listStep,
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        }
    )
);

public class ProcessFilter
{
    public string mainWindowTitle = null;
    public bool bContains = false;
}

public class StepConfig
{
    public string controlType { get; set; } = null;
    public string xPath { get; set; } = null;

    [DefaultValue(null)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string text { get; set; } = null;

    [DefaultValue(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool bRightClick { get; set; } = false;
}