' Resume Relay's screen app for Roku (Live Poster). The scene does the drawing; a task does the talking.
sub Main()
    screen = CreateObject("roSGScreen")
    port = CreateObject("roMessagePort")
    screen.setMessagePort(port)
    scene = screen.CreateScene("MainScene")
    screen.show()
    watchMemory(port)
    while true
        msg = wait(0, port)
        t = type(msg)
        if t = "roSGScreenEvent" and msg.isScreenClosed() then return
        if t = "roAppMemoryNotificationEvent" or t = "roDeviceInfoEvent" then memoryNote(msg)
    end while
end sub

' The Roku tells the app when memory runs low (before it would close it). The app holds very little (one video, and
' two pictures during the demo), so it just notes it in the debug log. Each call is guarded: older Roku OS versions
' don't have all of them.
sub watchMemory(port as object)
    m.memory = invalid
    m.device = invalid
    try
        m.memory = CreateObject("roAppMemoryMonitor")
        if m.memory <> invalid
            m.memory.SetMessagePort(port)
            m.memory.EnableMemoryWarningEvent(true)
            print "Memory: limit "; m.memory.GetChannelMemoryLimit(); ", available "; m.memory.GetChannelAvailableMemory(); ", used "; m.memory.GetMemoryLimitPercent(); "%"
        end if
    catch e
        print "Memory monitor: "; e.message
    end try
    try
        m.device = CreateObject("roDeviceInfo")
        m.device.SetMessagePort(port)
        m.device.EnableLowGeneralMemoryEvent(true)
    catch e
        print "Low memory events: "; e.message
    end try
end sub

sub memoryNote(msg as object)
    try
        if m.memory <> invalid then print "Memory warning: "; m.memory.GetMemoryLimitPercent(); "% of the limit used, "; m.memory.GetChannelAvailableMemory(); " available"
    catch e
    end try
end sub
