using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomersController : ControllerBase
{
    private readonly AppDbContext _context;

    public CustomersController(AppDbContext context)
    {
        _context = context;
    }

    // GET: api/customers
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var customers = await _context.Customers.ToListAsync();
        return Ok(customers);
    }

    // GET: api/customers/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var customer = await _context.Customers.FindAsync(id);

        if (customer == null)
            return NotFound();

        return Ok(customer);
    }

    // POST: api/customers
    [HttpPost]
    public async Task<IActionResult> Create(Customer customer)
    {
        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();

        return Ok(customer);
    }

    // GET: api/customers/addtest
    [HttpGet("addtest")]
    public async Task<IActionResult> AddTest()
    {
        var customer = new Customer
        {
            FullName = "Amir Nobahar",
            Mobile = "09123456789",
            TelegramId = "123456789",
            Username = "amir",
            Notes = "First Test Customer",
            IsBlocked = false
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();

        return Ok(customer);
    }

    // PUT: api/customers/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, Customer customer)
    {
        if (id != customer.Id)
            return BadRequest();

        var existingCustomer = await _context.Customers.FindAsync(id);

        if (existingCustomer == null)
            return NotFound();

        existingCustomer.FullName = customer.FullName;
        existingCustomer.Mobile = customer.Mobile;
        existingCustomer.TelegramId = customer.TelegramId;
        existingCustomer.Username = customer.Username;
        existingCustomer.Notes = customer.Notes;
        existingCustomer.IsBlocked = customer.IsBlocked;

        await _context.SaveChangesAsync();

        return Ok(existingCustomer);
    }

    // DELETE: api/customers/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var customer = await _context.Customers.FindAsync(id);

        if (customer == null)
            return NotFound();

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();

        return Ok("Customer Deleted Successfully");
    }
}