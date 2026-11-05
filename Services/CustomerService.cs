using DataBridge.Data;
using DataBridge.DTOs;
using DataBridge.Interfaces;
using DataBridge.Models;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

public class CustomerService : ICustomerService
{
    private readonly DataBridgeDbContext _db;

    public CustomerService(DataBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResponse<CustomerDto>> GetCustomersAsync(int page, int pageSize, CustomerSearchFilters filters)
    {
        IQueryable<Customer> query = _db.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filters.Location))
        {
            string location = filters.Location.Trim();
            query = query.Where(customer => customer.Location != null && customer.Location.Contains(location));
        }

        if (filters.Status != null)
        {
            query = query.Where(customer => customer.Status == filters.Status);
        }

        if (!string.IsNullOrWhiteSpace(filters.Company))
        {
            string company = filters.Company.Trim();
            query = query.Where(customer => customer.Company != null && customer.Company.Contains(company));
        }

        if (!string.IsNullOrWhiteSpace(filters.Name))
        {
            string name = filters.Name.Trim();
            query = query.Where(customer => customer.Name.Contains(name));
        }

        int totalCount = await query.CountAsync();
        int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var items = new List<CustomerDto>();

        // Skipping the query past the last page also stops a huge page number
        // from overflowing the Skip calculation.
        if (page <= totalPages)
        {
            List<Customer> customers = await query
                .OrderBy(customer => customer.Name)
                .ThenBy(customer => customer.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            foreach (Customer customer in customers)
            {
                items.Add(new CustomerDto
                {
                    Id = customer.Id,
                    Name = customer.Name,
                    Company = customer.Company,
                    Email = customer.Email,
                    Phone = customer.Phone,
                    Location = customer.Location,
                    Status = customer.Status.ToString(),
                    Notes = customer.Notes
                });
            }
        }

        return new PagedResponse<CustomerDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }
}
