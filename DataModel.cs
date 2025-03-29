using System;
using System.Collections.Generic;

namespace TaskTracker.Models
{
    public class ReminderTask
    {
        public int Id { get; set; }
        public long UserId { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime ReminderTime { get; set; }
    }

    public class Storage
    {
        public List<ReminderTask> Tasks { get; set; } = new List<ReminderTask>();
    }
}