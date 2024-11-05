using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Models
{
    public class ExecuteResult
    {
        public IRenderable Renderer { get; set; }
        public bool Exited { get; set; }
    }
}
